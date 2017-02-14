﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Learning.EventStore.Domain;

namespace Learning.EventStore.Cache
{
    public class CacheRepository : IRepository
    {
        private readonly IRepository _repository;
        private readonly IEventStore _eventStore;
        private readonly ICache _cache;
        private static readonly SemaphoreSlim SemaphoreSlim = new SemaphoreSlim(1, 1); 

        public CacheRepository(IRepository repository, IEventStore eventStore, ICache cache)
        {
            if (repository == null)
            {
                throw new ArgumentNullException(nameof(repository));
            }
            if (eventStore == null)
            {
                throw new ArgumentNullException(nameof(eventStore));
            }
            if (cache == null)
            {
                throw new ArgumentNullException(nameof(cache));
            }

            _repository = repository;
            _eventStore = eventStore;
            _cache = cache;
            _cache.RegisterEvictionCallback(key =>
            {
                SemaphoreSlim.Release();
            });
        }

        public async Task Save<T>(T aggregate, int? expectedVersion = null) where T : AggregateRoot
        {
            try
            {
                await SemaphoreSlim.WaitAsync();

                if (aggregate.Id != Guid.Empty && !_cache.IsTracked(aggregate.Id))
                {
                    _cache.Set(aggregate.Id, aggregate);
                }
                await _repository.Save(aggregate, expectedVersion);
            }
            catch (Exception)
            {
                _cache.Remove(aggregate.Id);
                throw;
            }
            finally
            {
                SemaphoreSlim.Release();
            }
        }

        public async Task<T> Get<T>(Guid aggregateId) where T : AggregateRoot
        {
            try
            {
                await SemaphoreSlim.WaitAsync();

                T aggregate;
                if (_cache.IsTracked(aggregateId))
                {
                    aggregate = (T)_cache.Get(aggregateId);
                    var events = await _eventStore.Get<T>(aggregateId, aggregate.Version);

                    if (events.Any() && events.First().Version != aggregate.Version + 1)
                    {
                        _cache.Remove(aggregateId);
                    }
                    else
                    {
                        aggregate.LoadFromHistory(events);
                        return aggregate;
                    }
                }

                aggregate = await _repository.Get<T>(aggregateId);
                _cache.Set(aggregateId, aggregate);
                return aggregate;
            }
            catch (Exception)
            {
                _cache.Remove(aggregateId);
                throw;
            }
            finally
            {
                SemaphoreSlim.Release();
            }
        }
    }
}