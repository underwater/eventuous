using System.Net.NetworkInformation;
using Eventuous.GooglePubSub.Shared;
using Eventuous.Subscriptions;
using Eventuous.Subscriptions.Consumers;
using Eventuous.Subscriptions.Context;
using Eventuous.Subscriptions.Diagnostics;
using Eventuous.Subscriptions.Filters;
using Google.Protobuf.Collections;
using static Google.Cloud.PubSub.V1.SubscriberClient;

namespace Eventuous.GooglePubSub.Subscriptions;

/// <summary>
/// Google PubSub subscription service
/// </summary>
[PublicAPI]
public class GooglePubSubSubscription
    : EventSubscription<PubSubSubscriptionOptions>, IMeasuredSubscription {
    public delegate ValueTask<Reply> HandleEventProcessingFailure(
        SubscriberClient client,
        PubsubMessage    pubsubMessage,
        Exception        exception
    );

    readonly HandleEventProcessingFailure _failureHandler;
    readonly SubscriptionName             _subscriptionName;
    readonly TopicName                    _topicName;

    SubscriberClient? _client;

    /// <summary>
    /// Creates a Google PubSub subscription service
    /// </summary>
    /// <param name="projectId">GCP project ID</param>
    /// <param name="topicId"></param>
    /// <param name="subscriptionId">Google PubSub subscription ID (within the project), which must already exist</param>
    /// <param name="consumePipe"></param>
    /// <param name="eventSerializer">Event serializer instance</param>
    /// <param name="loggerFactory">Optional: logger factory</param>
    public GooglePubSubSubscription(
        string            projectId,
        string            topicId,
        string            subscriptionId,
        ConsumePipe       consumePipe,
        IEventSerializer? eventSerializer = null,
        ILoggerFactory?   loggerFactory   = null
    ) : this(
        new PubSubSubscriptionOptions {
            SubscriptionId  = subscriptionId,
            ProjectId       = projectId,
            TopicId         = topicId,
            EventSerializer = eventSerializer
        },
        consumePipe,
        loggerFactory
    ) { }

    /// <summary>
    /// Creates a Google PubSub subscription service
    /// </summary>
    /// <param name="options">Subscription options <see cref="PubSubSubscriptionOptions"/></param>
    /// <param name="consumePipe"></param>
    /// <param name="loggerFactory">Optional: logger factory</param>
    public GooglePubSubSubscription(
        PubSubSubscriptionOptions options,
        ConsumePipe               consumePipe,
        ILoggerFactory?           loggerFactory = null
    ) : base(
        options,
        consumePipe,
        loggerFactory
    ) {
        _failureHandler = Ensure.NotNull(options, nameof(options)).FailureHandler
                       ?? DefaultEventProcessingErrorHandler;

        _subscriptionName = SubscriptionName.FromProjectSubscription(
            Ensure.NotEmptyString(options.ProjectId, nameof(options.ProjectId)),
            Ensure.NotEmptyString(options.SubscriptionId, nameof(options.SubscriptionId))
        );

        _topicName = TopicName.FromProjectTopic(
            options.ProjectId,
            Ensure.NotEmptyString(options.TopicId, nameof(options.TopicId))
        );
    }

    Task _subscriberTask = null!;

    protected override async ValueTask Subscribe(CancellationToken cancellationToken) {
        await CreateSubscription(
                _subscriptionName,
                _topicName,
                Options.ConfigureSubscription,
                cancellationToken
            )
            .NoContext();

        _client = await CreateAsync(
                _subscriptionName,
                Options.ClientCreationSettings,
                Options.Settings
            )
            .NoContext();

        _subscriberTask = _client.StartAsync(Handle);

        async Task<Reply> Handle(PubsubMessage msg, CancellationToken ct) {
            var eventType   = msg.Attributes[Options.Attributes.EventType];
            var contentType = msg.Attributes[Options.Attributes.ContentType];

            var evt = DeserializeData(
                contentType,
                eventType,
                msg.Data.ToByteArray(),
                _topicName.TopicId
            );

            var ctx = new MessageConsumeContext(
                msg.MessageId,
                eventType,
                contentType,
                _topicName.TopicId,
                0,
                msg.PublishTime.ToDateTime(),
                evt,
                AsMeta(msg.Attributes),
                ct
            );

            try {
                await Handler(ctx).NoContext();
                return Reply.Ack;
            }
            catch (Exception ex) {
                return await _failureHandler(_client, msg, ex).NoContext();
            }
        }

        Metadata AsMeta(MapField<string, string> attributes)
            => new(attributes.ToDictionary(x => x.Key, x => (object)x.Value));
    }

    protected override async ValueTask Unsubscribe(CancellationToken cancellationToken) {
        if (_client != null) await _client.StopAsync(cancellationToken).NoContext();
        await _subscriberTask.NoContext();
    }

    public async Task CreateSubscription(
        SubscriptionName      subscriptionName,
        TopicName             topicName,
        Action<Subscription>? configureSubscription,
        CancellationToken     cancellationToken
    ) {
        var emulator = Options.ClientCreationSettings.DetectEmulator();

        await PubSub.CreateTopic(topicName, emulator, Log, cancellationToken).NoContext();

        await PubSub.CreateSubscription(
            subscriptionName,
            topicName,
            configureSubscription,
            emulator,
            Log,
            cancellationToken
        ).NoContext();
    }

    static ValueTask<Reply> DefaultEventProcessingErrorHandler(
        SubscriberClient client,
        PubsubMessage    message,
        Exception        exception
    )
        => new(Reply.Nack);

    public GetSubscriptionGap GetMeasure() => new GooglePubSubGapMeasure(Options).GetSubscriptionGap;
}