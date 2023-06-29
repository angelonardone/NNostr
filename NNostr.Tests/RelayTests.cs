using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.TestHost;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using Xunit;
using Xunit.Abstractions;

namespace NNostr.Tests;

public class RelayTests
{
    private readonly ITestOutputHelper _output;

    public RelayTests(ITestOutputHelper output )
    {
        _output = output;
    }

    public class TestServerClient : NostrClient
    {
        private readonly TestServer _server;

        public TestServerClient(TestServer server) : base(new Uri("wss://lol.com"), null)
        {
            _server = server;
        }

        protected override Task<WebSocket> Connect()
        {
            var r = _server.CreateWebSocketClient();
            return r.ConnectAsync(_server.BaseAddress, _cts.Token);
        }
    }

    [Fact]
    public async Task CanUseBasics()
    {
        await using var server = new RelayTestServer(_output,true);
        var client = new TestServerClient(server.Server);
        await client.Connect();
        var p = ECPrivKey.Create(RandomUtils.GetBytes(32));


        var event1 = new NostrEvent()
        {
            Kind = 1,
            Content = $"testing NNostr",
        };

        await event1.ComputeIdAndSignAsync(p);
        var tcseose = new TaskCompletionSource<string>();
        client.MessageReceived+= (sender, s) =>
        {
            Console.WriteLine("sdsd");
            
        };
        client.EoseReceived += (sender, s) =>
        {
            tcseose.TrySetResult(s);
        };
        
        await client.Connect();


        await client.CreateSubscription("TESTX", new[]
        {
            new NostrSubscriptionFilter()
            {
                Authors = new[] {event1.PublicKey}
            }
        }, CancellationToken.None);
        
        var evts =  client.SubscribeForEvents(new []{ new NostrSubscriptionFilter()
        {
            Authors = new []{event1.PublicKey}
        }}, true, CancellationToken.None);
        
        
        await client.SendEventsAndWaitUntilReceived(new[] {event1}, CancellationToken.None);
        
        Assert.Equal("TESTX", await tcseose.Task);
        var matched = await evts.SingleAsync();
        Assert.Equal(matched.Id, event1.Id);

    }

    [Fact]
    public async Task CanUseReplaceableEvents()
    {
        await using var server = new RelayTestServer(_output, true);
        var client = new TestServerClient(server.Server);
        await client.Connect();
        var p = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var pub = p.CreateXOnlyPubKey().ToHex();
        var replaceableEvent = new NostrEvent()
        {
            Kind = 15000,
            Content = $"testing NNostr replaceable",
        };

        await replaceableEvent.ComputeIdAndSignAsync(p);

        await client.SendEventsAndWaitUntilReceived(new[] {replaceableEvent}, CancellationToken.None);
        Assert.Equal(1, await client.SubscribeForEvents(new[]
        {
            new NostrSubscriptionFilter()
            {
                Kinds = new[] {15000},
                Authors = new[] {pub}
            }
        }, true, CancellationToken.None).CountAsync());

        replaceableEvent = new NostrEvent()
        {
            Kind = 15000,
            Content = $"testing NNostr replaceable2",
        };

        await replaceableEvent.ComputeIdAndSignAsync(p);
        await client.SendEventsAndWaitUntilReceived(new[] {replaceableEvent}, CancellationToken.None);
        var evts = await client.SubscribeForEvents(new[]
        {
            new NostrSubscriptionFilter()
            {
                Kinds = new[] {15000},
                Authors = new[] {pub}
            }
        }, true, CancellationToken.None).ToArrayAsync();
        
        Assert.Equal("testing NNostr replaceable2", Assert.Single(evts).Content);
    }
    
    [Fact]
public async Task CanHandleSubscriptionsAndFilters()
{
    await using var server = new RelayTestServer(_output, true);
    var client = new TestServerClient(server.Server);
    await client.Connect();

    var user1 = ECPrivKey.Create(RandomUtils.GetBytes(32));
    var user1Pub = user1.CreateXOnlyPubKey().ToHex();
    var user2 = ECPrivKey.Create(RandomUtils.GetBytes(32));
    var user2Pub = user2.CreateXOnlyPubKey().ToHex();

    var filtersForSubscription1 = new[]
    {
        new NostrSubscriptionFilter()
        {
            Authors = new[] {user1Pub},
            Kinds = new[] {1, 2, 3},
            Limit = 2
        },
        new NostrSubscriptionFilter()
        {
            Authors = new[] {user2Pub},
            Kinds = new[] {4, 5, 6},
            Limit = 1
        }
    };

    var eventsThatFitSubscriptionFilter1 = new List<NostrEvent>()
    {
        await new NostrEvent()
        {
            Kind = 1,
            Content = "test content",
        }.ComputeIdAndSignAsync(user1),
        
        await new NostrEvent()
        {
            Kind = 2,
            Content = "test content 2",
        }.ComputeIdAndSignAsync(user1),
        await new NostrEvent()
        {
            Kind = 3,
            Content = "test content 3",
            CreatedAt = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1))
        }.ComputeIdAndSignAsync(user1)
    };
    
    var eventsThatFitSubscriptionFilter2 = new List<NostrEvent>()
    {
        
        await new NostrEvent()
        {
            Kind = 4,
            Content = "test content",
        }.ComputeIdAndSignAsync(user2),
        
        await new NostrEvent()
        {
            Kind = 5,
            Content = "test content 2",
            CreatedAt = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1))
        }.ComputeIdAndSignAsync(user2),
        await new NostrEvent()
        {
            Kind = 6,
            Content = "test content 3",
            CreatedAt = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1))
        }.ComputeIdAndSignAsync(user2)
    };

    var randomEvents = new List<NostrEvent>()
    {
        await new NostrEvent()
        {
            Kind = 10,
            Content = "test content random1",
        }.ComputeIdAndSignAsync(ECPrivKey.Create(RandomUtils.GetBytes(32))),
        await new NostrEvent()
        {
            Kind = 20,
            Content = "test content random2",
        }.ComputeIdAndSignAsync(ECPrivKey.Create(RandomUtils.GetBytes(32)))
    };
    foreach (var evt in eventsThatFitSubscriptionFilter1.Concat(eventsThatFitSubscriptionFilter2).Concat(randomEvents).Shuffle())
    {

        await client.PublishEvent(evt);
    }
    var evtsForSubscripion1 = new ConcurrentBag<NostrEvent>();
    // Wait for EOSE message
    var subscription1EoseReceived = new TaskCompletionSource<bool>();
    client.EoseReceived += (sender, subscriptionId) =>
    {
        if (subscriptionId == "subscription_1")
        {
            Assert.Equal(3, evtsForSubscripion1.Count);
            Assert.Contains(evtsForSubscripion1, @event => @event.Id == eventsThatFitSubscriptionFilter1[0].Id);
            Assert.Contains(evtsForSubscripion1, @event => @event.Id == eventsThatFitSubscriptionFilter1[1].Id);
            Assert.Contains(evtsForSubscripion1, @event => @event.Id == eventsThatFitSubscriptionFilter2[0].Id);
            subscription1EoseReceived.SetResult(true);
        }
    };
    client.EventsReceived += (sender, events) =>
    {
        if (events.subscriptionId == "subscription_1")
        {
            foreach (var evt in events.events)
            {
                evtsForSubscripion1.Add(evt);
            }
        }
    };
    // Create a subscription with multiple filters
    await client.CreateSubscription("subscription_1", filtersForSubscription1, CancellationToken.None);



    // Wait for EOSE message to be received
    await subscription1EoseReceived.Task;

}

}