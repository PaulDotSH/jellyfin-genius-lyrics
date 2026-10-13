using System;
using System.Threading;
using System.Threading.Tasks;

namespace GeniusLyricsPlugin.Services
{
    // Serialises requests to one host: callers wait their turn, at least minSpacing apart, and Defer pauses everyone.
    public sealed class RequestThrottle : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _timingLock = new();
        private DateTime _nextRequestUtc = DateTime.MinValue;

        public async Task WaitAsync(TimeSpan minSpacing, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    TimeSpan delay;
                    lock (_timingLock)
                    {
                        delay = _nextRequestUtc - DateTime.UtcNow;
                        if (delay <= TimeSpan.Zero)
                        {
                            _nextRequestUtc = DateTime.UtcNow + (minSpacing > TimeSpan.Zero ? minSpacing : TimeSpan.Zero);
                            return;
                        }
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Defer(TimeSpan wait)
        {
            lock (_timingLock)
            {
                var until = DateTime.UtcNow + wait;
                if (until > _nextRequestUtc)
                {
                    _nextRequestUtc = until;
                }
            }
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }
}
