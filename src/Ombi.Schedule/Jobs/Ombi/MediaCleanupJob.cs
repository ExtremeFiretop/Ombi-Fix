using System;
using System.Threading.Tasks;
using Ombi.Core.Engine.Interfaces;
using Quartz;

namespace Ombi.Schedule.Jobs.Ombi
{
    public class MediaCleanupJob : IMediaCleanupJob
    {
        private readonly IMediaCleanupEngine _engine;

        public MediaCleanupJob(IMediaCleanupEngine engine)
        {
            _engine = engine;
        }

        public Task Execute(IJobExecutionContext context)
        {
            return _engine.ProcessPending();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
