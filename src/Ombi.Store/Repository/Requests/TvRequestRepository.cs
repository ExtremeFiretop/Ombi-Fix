using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ombi.Helpers;
using Ombi.Store.Context;
using Ombi.Store.Entities.Requests;

namespace Ombi.Store.Repository.Requests
{
    public class TvRequestRepository : BaseRepository<TvRequests, OmbiContext>, ITvRequestRepository
    {
        public TvRequestRepository(OmbiContext ctx) : base(ctx)
        {
            Db = ctx;
        }

        public OmbiContext Db { get; }

        public async Task<TvRequests> GetRequestAsync(int tvDbId)
        {
            return await Db.TvRequests.Where(x => x.ExternalProviderId == tvDbId)
                .Include(x => x.ChildRequests)
                    .ThenInclude(x => x.RequestedUser)
                .Include(x => x.ChildRequests)
                    .ThenInclude(x => x.SeasonRequests)
                    .ThenInclude(x => x.Episodes)
                .FirstOrDefaultAsync();
        }

        public TvRequests GetRequest(int theMovieDbId)
        {
            return Db.TvRequests.Where(x => x.ExternalProviderId == theMovieDbId).AsSplitQuery()
                .Include(x => x.ChildRequests)
                    .ThenInclude(x => x.RequestedUser)
                .Include(x => x.ChildRequests)
                    .ThenInclude(x => x.SeasonRequests)
                    .ThenInclude(x => x.Episodes)
                .FirstOrDefault();
        }

        public IQueryable<TvRequests> Get()
        {
            return Db.TvRequests
                .Include(x => x.ChildRequests)
                .ThenInclude(x => x.RequestedUser)
                .Include(x => x.ChildRequests)
                .ThenInclude(x => x.SeasonRequests)
                .ThenInclude(x => x.Episodes)
                .AsQueryable();
        }

        public IQueryable<TvRequests> Get(string userId)
        {
            return Db.TvRequests
                .Include(x => x.ChildRequests)
                .ThenInclude(x => x.RequestedUser)
                .Include(x => x.ChildRequests)
                .ThenInclude(x => x.SeasonRequests)
                .ThenInclude(x => x.Episodes)
                .Where(x => x.ChildRequests.Any(a => a.RequestedUserId == userId))
                .AsQueryable();
        }

        public IQueryable<TvRequests> GetLite(string userId)
        {
            return Db.TvRequests
                .Include(x => x.ChildRequests)
                .ThenInclude(x => x.RequestedUser)
                .Where(x => x.ChildRequests.Any(a => a.RequestedUserId == userId))
                .AsQueryable();
        }

        public IQueryable<TvRequests> GetLite()
        {
            return Db.TvRequests
                .Include(x => x.ChildRequests)
                .ThenInclude(x => x.RequestedUser)
                .AsQueryable();
        }

        public IQueryable<ChildRequests> GetChild()
        {
            return Db.ChildRequests
                .Include(x => x.RequestedUser)
                .Include(x => x.ParentRequest)
                .Include(x => x.SeasonRequests)
                .ThenInclude(x => x.Episodes)
                .AsQueryable();
        }

        public IQueryable<ChildRequests> GetChild(string userId)
        {
            return Db.ChildRequests
                .Where(x => x.RequestedUserId == userId)
                .Include(x => x.RequestedUser)
                .Include(x => x.ParentRequest)
                .Include(x => x.SeasonRequests)
                .ThenInclude(x => x.Episodes)
                .AsQueryable();
        }

        public async Task MarkChildAsAvailable(int id)
        {
            var request = new ChildRequests { Id = id, Available = true, MarkedAsAvailable = DateTime.UtcNow };
            var attached = Db.ChildRequests.Attach(request);
            attached.Property(x => x.Available).IsModified = true;
            attached.Property(x => x.MarkedAsAvailable).IsModified = true;
            await Db.SaveChangesAsync();
        }

        public async Task MarkEpisodeAsAvailable(int id)
        {
            var request = new EpisodeRequests { Id = id, Available = true };
            var attached = Db.EpisodeRequests.Attach(request);
            attached.Property(x => x.Available).IsModified = true;
            await Db.SaveChangesAsync();
        }

        public async Task Save()
        {
            await InternalSaveChanges();
        }

        public async Task<ChildRequests> AddChild(ChildRequests request)
        {
            await Db.ChildRequests.AddAsync(request);
            await InternalSaveChanges();

            return request;
        }

        public async Task DeleteChild(ChildRequests request)
        {
            if (request == null)
            {
                return;
            }

            await EnsureChildGraphLoaded(request);
            MarkChildGraphForDeletion(request);
            await InternalSaveChanges();
            await CleanupOrphanedRequestData();
        }

        public async Task DeleteRequest(TvRequests request)
        {
            if (request == null)
            {
                return;
            }

            var children = request.ChildRequests;
            if (children == null)
            {
                children = await GetChild().Where(x => x.ParentRequestId == request.Id).ToListAsync();
            }

            foreach (var child in children.ToList())
            {
                await EnsureChildGraphLoaded(child);
                MarkChildGraphForDeletion(child);
            }

            Db.TvRequests.Remove(request);
            await InternalSaveChanges();
            await CleanupOrphanedRequestData();
        }

        public async Task DeleteChildRange(IEnumerable<ChildRequests> request)
        {
            var children = request?.ToList() ?? new List<ChildRequests>();
            foreach (var child in children)
            {
                await EnsureChildGraphLoaded(child);
                MarkChildGraphForDeletion(child);
            }

            if (children.Count > 0)
            {
                await InternalSaveChanges();
            }

            await CleanupOrphanedRequestData();
        }

        public async Task<int> CleanupOrphanedRequestData()
        {
            var removed = 0;

            // Work from the leaves upward and persist each stage before evaluating the next one.
            // This lets the same maintenance pass collapse legacy graphs such as:
            // episode -> missing season, season -> no episodes, child -> no seasons, parent -> no children.
            var orphanEpisodes = await Db.EpisodeRequests
                .Where(x => !Db.Set<SeasonRequests>().Any(s => s.Id == x.SeasonId))
                .ToListAsync();
            if (orphanEpisodes.Count > 0)
            {
                Db.EpisodeRequests.RemoveRange(orphanEpisodes);
                removed += orphanEpisodes.Count;
                await InternalSaveChanges();
            }

            var staleSeasons = await Db.Set<SeasonRequests>()
                .Where(x => !Db.ChildRequests.Any(c => c.Id == x.ChildRequestId)
                         || !Db.EpisodeRequests.Any(e => e.SeasonId == x.Id))
                .ToListAsync();
            if (staleSeasons.Count > 0)
            {
                Db.Set<SeasonRequests>().RemoveRange(staleSeasons);
                removed += staleSeasons.Count;
                await InternalSaveChanges();
            }

            var staleChildren = await Db.ChildRequests
                .Where(x => !Db.TvRequests.Any(t => t.Id == x.ParentRequestId)
                         || !Db.Set<SeasonRequests>().Any(s => s.ChildRequestId == x.Id))
                .ToListAsync();
            if (staleChildren.Count > 0)
            {
                Db.ChildRequests.RemoveRange(staleChildren);
                removed += staleChildren.Count;
                await InternalSaveChanges();
            }

            var emptyParents = await Db.TvRequests
                .Where(x => !Db.ChildRequests.Any(c => c.ParentRequestId == x.Id))
                .ToListAsync();
            if (emptyParents.Count > 0)
            {
                Db.TvRequests.RemoveRange(emptyParents);
                removed += emptyParents.Count;
                await InternalSaveChanges();
            }

            return removed;
        }

        private async Task EnsureChildGraphLoaded(ChildRequests request)
        {
            if (request.SeasonRequests != null)
            {
                return;
            }

            request.SeasonRequests = await Db.Set<SeasonRequests>()
                .Where(x => x.ChildRequestId == request.Id)
                .Include(x => x.Episodes)
                .ToListAsync();
        }

        private void MarkChildGraphForDeletion(ChildRequests request)
        {
            var seasons = request.SeasonRequests?.ToList() ?? new List<SeasonRequests>();
            var episodes = seasons
                .Where(x => x.Episodes != null)
                .SelectMany(x => x.Episodes)
                .ToList();

            if (episodes.Count > 0)
            {
                Db.EpisodeRequests.RemoveRange(episodes);
            }

            if (seasons.Count > 0)
            {
                Db.Set<SeasonRequests>().RemoveRange(seasons);
            }

            Db.ChildRequests.Remove(request);
        }

        public async Task Update(TvRequests request)
        {
            Db.Update(request);

            await InternalSaveChanges();
        }

        public async Task UpdateChild(ChildRequests request)
        {
            Db.Update(request);

            await InternalSaveChanges();
        }
    }
}