using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;

namespace Eventuous.Subscriptions.EventStoreDB {
    /// <summary>
    /// Catch-up subscription for EventStoreDB, using the $all global stream
    /// </summary>
    [PublicAPI]
    public class AllStreamSubscription : EventStoreSubscriptionService {
        readonly IEventFilter                 _eventFilter;
        readonly AllStreamSubscriptionOptions _options;

        /// <summary>
        /// Creates EventStoreDB catch-up subscription service for $all
        /// </summary>
        /// <param name="eventStoreClient">EventStoreDB gRPC client instance</param>
        /// <param name="subscriptionId">Subscription ID</param>
        /// <param name="checkpointStore">Checkpoint store instance</param>
        /// <param name="eventSerializer">Event serializer instance</param>
        /// <param name="eventHandlers">Collection of event handlers</param>
        /// <param name="loggerFactory">Optional: logger factory</param>
        /// <param name="eventFilter">Optional: server-side event filter</param>
        /// <param name="measure">Optional: gap measurement for metrics</param>
        public AllStreamSubscription(
            EventStoreClient           eventStoreClient,
            string                     subscriptionId,
            ICheckpointStore           checkpointStore,
            IEnumerable<IEventHandler> eventHandlers,
            IEventSerializer?          eventSerializer = null,
            ILoggerFactory?            loggerFactory   = null,
            IEventFilter?              eventFilter     = null,
            ISubscriptionGapMeasure?   measure         = null
        ) : this(
            eventStoreClient,
            new AllStreamSubscriptionOptions { SubscriptionId = subscriptionId },
            checkpointStore,
            eventHandlers,
            eventSerializer,
            loggerFactory,
            eventFilter,
            measure
        ) { }

        /// <summary>
        /// Creates EventStoreDB catch-up subscription service for $all
        /// </summary>
        /// <param name="eventStoreClient"></param>
        /// <param name="options"></param>
        /// <param name="checkpointStore">Checkpoint store instance</param>
        /// <param name="eventSerializer">Event serializer instance</param>
        /// <param name="eventHandlers">Collection of event handlers</param>
        /// <param name="loggerFactory">Optional: logger factory</param>
        /// <param name="eventFilter">Optional: server-side event filter</param>
        /// <param name="measure">Optional: gap measurement for metrics</param>
        public AllStreamSubscription(
            EventStoreClient             eventStoreClient,
            AllStreamSubscriptionOptions options,
            ICheckpointStore             checkpointStore,
            IEnumerable<IEventHandler>   eventHandlers,
            IEventSerializer?            eventSerializer = null,
            ILoggerFactory?              loggerFactory   = null,
            IEventFilter?                eventFilter     = null,
            ISubscriptionGapMeasure?     measure         = null
        ) : base(
            eventStoreClient,
            options,
            checkpointStore,
            eventHandlers,
            eventSerializer,
            loggerFactory,
            measure
        ) {
            _eventFilter = eventFilter ?? EventTypeFilter.ExcludeSystemEvents();
            _options     = options;
        }

        protected override async Task<EventSubscription> Subscribe(
            Checkpoint        checkpoint,
            CancellationToken cancellationToken
        ) {
            var filterOptions = new SubscriptionFilterOptions(
                _eventFilter,
                10,
                (_, p, ct) => StoreCheckpoint(new EventPosition(p.CommitPosition, DateTime.Now), ct)
            );

            var sub = checkpoint.Position != null
                ? await EventStoreClient.SubscribeToAllAsync(
                    new Position(checkpoint.Position.Value, checkpoint.Position.Value),
                    HandleEvent,
                    false,
                    HandleDrop,
                    filterOptions,
                    _options.ConfigureOperation,
                    _options.Credentials,
                    cancellationToken
                )
                : await EventStoreClient.SubscribeToAllAsync(
                    HandleEvent,
                    false,
                    HandleDrop,
                    filterOptions,
                    _options.ConfigureOperation,
                    _options.Credentials,
                    cancellationToken
                );

            return new EventSubscription(SubscriptionId, new Stoppable(() => sub.Dispose()));

            Task HandleEvent(EventStore.Client.StreamSubscription _, ResolvedEvent re, CancellationToken ct)
                => Handler(AsReceivedEvent(re), ct);

            void HandleDrop(EventStore.Client.StreamSubscription _, SubscriptionDroppedReason reason, Exception? ex)
                => Dropped(EsdbMappings.AsDropReason(reason), ex);

            static ReceivedEvent AsReceivedEvent(ResolvedEvent re)
                => new() {
                    EventId        = re.Event.EventId.ToString(),
                    GlobalPosition = re.Event.Position.CommitPosition,
                    StreamPosition = re.Event.Position.CommitPosition,
                    Stream         = re.OriginalStreamId,
                    Sequence       = re.Event.EventNumber,
                    Created        = re.Event.Created,
                    EventType      = re.Event.EventType,
                    Data           = re.Event.Data,
                    Metadata       = re.Event.Metadata
                };
        }
    }
}