using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ombi.Core.Rule.Interfaces;
using Ombi.Core.Engine;
using Ombi.Store.Entities;
using Ombi.Store.Entities.Requests;
using Ombi.Store.Repository.Requests;

namespace Ombi.Core.Rule.Rules.Request
{
    public class ExistingTvRequestRule : BaseRequestRule, IRules<BaseRequest>
    {
        public ExistingTvRequestRule(ITvRequestRepository rv)
        {
            Tv = rv;
        }

        private ITvRequestRepository Tv { get; }

        /// <summary>
        /// We check if the request exists, if it does then we don't want to re-request it.
        /// </summary>
        /// <param name="obj">The object.</param>
        /// <returns></returns>
        public async Task<RuleResult> Execute(BaseRequest obj)
        {
            if (obj.RequestType == RequestType.TvShow)
            {
                var tv = (ChildRequests) obj;
                var currentRequests = await Tv.GetChild()
                    .Where(x => x.ParentRequest.ExternalProviderId == tv.Id) // the Id on the child is TheMovieDb at this point
                    .ToListAsync();
                if (currentRequests.Count == 0)
                {
                    return Success();
                }

                foreach (var season in tv.SeasonRequests)
                {
                    var existingEpisodeNumbers = currentRequests
                        .SelectMany(x => x.SeasonRequests ?? new List<SeasonRequests>())
                        .Where(x => x.SeasonNumber == season.SeasonNumber)
                        .SelectMany(x => x.Episodes ?? new List<EpisodeRequests>())
                        .Select(x => x.EpisodeNumber)
                        .ToHashSet();

                    season.Episodes.RemoveAll(x => existingEpisodeNumbers.Contains(x.EpisodeNumber));
                }

                var anyEpisodes = tv.SeasonRequests.SelectMany(x => x.Episodes).Any();

                if (!anyEpisodes)
                {
                    return Fail(ErrorCode.EpisodesAlreadyRequested, $"We already have episodes requested from series {tv.Title}");
                }

            }
            return Success();
        }
    }
}