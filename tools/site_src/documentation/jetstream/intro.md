# JetStream

[JetStream](https://docs.nats.io/nats-concepts/jetstream) is the built-in distributed persistence system which enables
new functionalities and higher qualities of service on top of the base _Core NATS_ functionalities and qualities of service.

JetStream is built-in to nats-server and you only need 1 (or 3 or 5 if you want fault-tolerance against 1 or 2
simultaneous NATS server failures) of your NATS server(s) to be JetStream enabled for it to be available to all the
client applications.

JetStream can be enabled by running the server with `-js` flag e.g. `nats-server -js`.

## Streaming: temporal decoupling between the publishers and subscribers

One of the tenets of basic publish/subscribe messaging is that there is a required temporal coupling between the
publishers and the subscribers: subscribers only receive the messages that are published when they are actively
connected to the messaging system (i.e. they do not receive messages that are published while they are not subscribing
or not running or disconnected).

Streams capture and store messages published on one (or more) subject and allow client applications to create
consumers at any time to 'replay' (or consume) all or some of the messages stored in the stream.

Streams are message stores, each stream defines how messages are stored and what the limits (duration, size, interest)
of the retention are. Streams consume normal NATS subjects, any message published on those subjects will be captured
in the defined storage system.

A consumer is a stateful view of a stream. It acts as interface for clients to consume a subset of messages stored in a
stream and will keep track of which messages were delivered and acknowledged by clients.

## JetStream Quick Start

[Download the latest](https://nats.io/download/) `nats-server` for your platform and run it with JetStream enabled:

```shell
$ nats-server -js
```
or
```shell
$ docker run nats -js
```

Install [NATS.Net](https://www.nuget.org/packages/NATS.Net) from Nuget.

Before we can do anything, we need a JetStream context:

[!code-csharp[](../../../../tests/NATS.Net.DocsExamples/JetStream/IntroPage.cs#js-connection)]

Let's create our stream first. In JetStream, a stream is simply a storage for messages:

[!code-csharp[](../../../../tests/NATS.Net.DocsExamples/JetStream/IntroPage.cs#js-stream)]

We can save messages in a stream by publishing them to the subjects the stream is interested in, which is `orders.>` in
our case, meaning any subject prefixed with `orders.` e.g. `orders.new.123`. Have a look at NATS documentation about
[wildcards in Subject-Based Messaging](https://docs.nats.io/nats-concepts/subjects#wildcards) for more information.

Given that we have a record `Order`, we can publish and consume stream of `Order` objects:

[!code-csharp[](../../../../tests/NATS.Net.DocsExamples/JetStream/IntroPage.cs#order-class)]

We can publish to `SHOP_ORDERS` stream and receive a confirmation that our message is persisted:

[!code-csharp[](../../../../tests/NATS.Net.DocsExamples/JetStream/IntroPage.cs#js-publish)]

Now that we have a few messages in our stream, let's see its status using the [NATS command
line client](https://github.com/nats-io/natscli):

```shell
$ nats stream ls
╭───────────────────────────────────────────────────────────────────────────────────╮
│                                      Streams                                      │
├─────────────┬─────────────┬─────────────────────┬──────────┬───────┬──────────────┤
│ Name        │ Description │ Created             │ Messages │ Size  │ Last Message │
├─────────────┼─────────────┼─────────────────────┼──────────┼───────┼──────────────┤
│ SHOP_ORDERS │             │ 2023-09-12 10:25:52 │ 10       │ 600 B │ 10.41s       │
╰─────────────┴─────────────┴─────────────────────┴──────────┴───────┴──────────────╯
```

We need one more JetStream construct before we can start consuming our messages: a *consumer*:

[!code-csharp[](../../../../tests/NATS.Net.DocsExamples/JetStream/IntroPage.cs#js-consumer)]

In JetStream, consumers are stored on the server. Clients don't need to worry about maintaining state separately.
You can think of JetStream consumers as pointers to messages in streams stored on the NATS JetStream server. Let's
see what our consumer's state is:

```shell
$ nats consumer report SHOP_ORDERS
╭────────────────────────────────────────────────────────────────────────────────────────────────────────────────╮
│                                Consumer report for SHOP_ORDERS with 1 consumers                                │
├─────────────────┬──────┬────────────┬──────────┬─────────────┬─────────────┬─────────────┬───────────┬─────────┤
│ Consumer        │ Mode │ Ack Policy │ Ack Wait │ Ack Pending │ Redelivered │ Unprocessed │ Ack Floor │ Cluster │
├─────────────────┼──────┼────────────┼──────────┼─────────────┼─────────────┼─────────────┼───────────┼─────────┤
│ order_processor │ Pull │ Explicit   │ 30.00s   │ 0           │ 0           │ 10 / 100%   │ 0         │         │
╰─────────────────┴──────┴────────────┴──────────┴─────────────┴─────────────┴─────────────┴───────────┴─────────╯
```

Check out [JetStream documentation](https://docs.nats.io/nats-concepts/jetstream) for more information on streams and consumers.

Finally, we're ready to consume the messages we persisted in `SHOP_ORDERS` stream:

[!code-csharp[](../../../../tests/NATS.Net.DocsExamples/JetStream/IntroPage.cs#consumer-consume)]

## What's Next

[Managing JetStream](manage.md) covers how to create, update, get, list and delete streams and consumers.

[Publishing messages to streams](publish.md) is achieved by simply publishing to a subject where a stream is configured
to be interested in that subject.

[Consuming messages from streams](consume.md) explains different ways of retrieving persisted messages.

[Serialization](../serialization.md) covers how to configure serializers for your messages.
