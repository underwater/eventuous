using System.Threading.Tasks;
using Eventuous.EventStoreDB.Subscriptions;
using Eventuous.Projections.MongoDB.Tools;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Eventuous.Projections.MongoDB {
    [PublicAPI]
    public abstract class MongoProjection<T> : IEventHandler
        where T : ProjectedDocument {
        readonly ILogger? _log;

        protected IMongoCollection<T> Collection { get; }

        protected MongoProjection(IMongoDatabase database, string subscriptionGroup, ILoggerFactory loggerFactory) {
            var log = loggerFactory.CreateLogger(GetType());
            _log              = log.IsEnabled(LogLevel.Debug) ? log : null;
            SubscriptionGroup = subscriptionGroup;
            Collection        = database.GetDocumentCollection<T>();
        }

        public string SubscriptionGroup { get; }

        public async Task HandleEvent(object evt, long? position) {
            var updateTask = GetUpdate(evt);
            var update     = updateTask == NoOp ? null : await updateTask;

            if (update == null) {
                _log?.LogDebug("No handler for {Event}", evt.GetType().Name);
                return;
            }

            update.Update.Set(x => x.Position, position);

            _log?.LogDebug("Projecting {Event}", evt);
            await Collection.UpdateOneAsync(update.Filter, update.Update, new UpdateOptions { IsUpsert = true });
        }

        protected abstract ValueTask<UpdateOperation<T>> GetUpdate(object evt);

        protected UpdateOperation<T> Operation(BuildFilter<T> filter, BuildUpdate<T> update)
            => new(filter(Builders<T>.Filter), update(Builders<T>.Update));

        protected ValueTask<UpdateOperation<T>> OperationTask(BuildFilter<T> filter, BuildUpdate<T> update)
            => new(Operation(filter, update));

        protected UpdateOperation<T> Operation(string id, BuildUpdate<T> update)
            => Operation(filter => filter.Eq(x => x.Id, id), update);

        protected ValueTask<UpdateOperation<T>> OperationTask(string id, BuildUpdate<T> update)
            => new(Operation(id, update));

        protected static readonly ValueTask<UpdateOperation<T>> NoOp = new((UpdateOperation<T>) null!);
    }

    public record UpdateOperation<T>(FilterDefinition<T> Filter, UpdateDefinition<T> Update);
}