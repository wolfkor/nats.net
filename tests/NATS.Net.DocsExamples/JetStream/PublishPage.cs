// ReSharper disable SuggestVarOrType_Elsewhere

using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

#pragma warning disable SA1123
#pragma warning disable SA1124
#pragma warning disable SA1509
#pragma warning disable SA1515

namespace NATS.Net.DocsExamples.JetStream;

public class PublishPage
{
    public async Task Run()
    {
        Console.WriteLine("____________________________________________________________");
        Console.WriteLine("NATS.Net.DocsExamples.JetStream.PublishPage");

        try
        {
            await using NatsConnection nats1 = new NatsConnection();
            NatsJSContext js1 = new NatsJSContext(nats1);
            await js1.DeleteStreamAsync("SHOP_ORDERS");
            await Task.Delay(1000);
        }
        catch (NatsJSApiException)
        {
        }

        try
        {
            await using NatsConnection nats1 = new NatsConnection();
            NatsJSContext js1 = new NatsJSContext(nats1);
            await js1.DeleteStreamAsync("ORDERS");
            await Task.Delay(1000);
        }
        catch (NatsJSApiException)
        {
        }

        {
            #region js
            await using NatsClient nc = new NatsClient();
            INatsJSContext js = nc.CreateJetStreamContext();

            await js.CreateStreamAsync(new StreamConfig(name: "ORDERS", subjects: ["orders.>"]));
            #endregion
        }

        {
            #region publish
            await using NatsClient nc = new NatsClient();
            INatsJSContext js = nc.CreateJetStreamContext();

            Order order = new Order { Id = 1 };

            PubAckResponse ack = await js.PublishAsync("orders.new.1", order);

            ack.EnsureSuccess();
            #endregion
        }

        {
            #region publish-duplicate
            await using NatsClient nc = new NatsClient();
            INatsJSContext js = nc.CreateJetStreamContext();

            Order order = new Order { Id = 1 };

            PubAckResponse ack = await js.PublishAsync(subject: "orders.new.1", data: order, opts: new NatsJSPubOpts { MsgId = "1" });
            if (ack.Duplicate)
            {
                // A message with the same ID was published before
            }
            #endregion
        }
    }
}
