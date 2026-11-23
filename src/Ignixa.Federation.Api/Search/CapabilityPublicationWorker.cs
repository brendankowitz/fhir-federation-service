namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The sole producer of a proof-backed host's capabilities view: admits it from an <see cref="ICapabilityAdmissionSource"/>,
/// builds the generation and installs it through the source's atomic commit, so no stale admission is ever installed.
/// </summary>
/// <remarks>
/// <para>
/// One serial loop. The first attempt is immediate and first withdraws whatever the state holds, including a registered
/// seed, so the host is cold until this worker installs an admission. While the installed admission is current the worker
/// only waits; once it is stale (requests already treat it as cold) the worker withdraws it and probes again at once. A
/// refusal or a commit the source declines leaves the host cold until the next attempt one cadence later, never sooner.
/// The generation is built before the commit, outside the source's evidence gate; the commit action only checks the
/// lifecycle and swaps it in.
/// </para>
/// <para>
/// <see cref="StopAsync"/> first closes the publication path and withdraws the view, then cancels and joins the loop
/// within the host's stop bound. A closed or cancelled loop starts no new probe. A result returned after cancellation,
/// even a valid one, is ignored, and a closed path refuses any install that is still pending. A defect from the source or
/// a failed build stops the loop and the host stays cold.
/// </para>
/// <para>
/// Not registered by default: a host that activates it must not also publish through <see cref="IFederationCapabilityPublisher"/>.
/// </para>
/// </remarks>
internal sealed class CapabilityPublicationWorker : BackgroundService
{
    // A timer truncates to whole milliseconds, so a shorter positive cadence would be a zero delay; Task.Delay rejects longer.
    private static readonly TimeSpan MinCadence = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxCadence = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly PrimarySearchState _state;
    private readonly ICapabilityAdmissionSource _source;
    private readonly TimeSpan _cadence;
    private readonly TimeProvider _time;

    // Orders installs (inside the source's commit) with withdrawals and the terminal close. Held only for state swaps.
    private readonly Lock _lifecycle = new();
    private bool _closed;
    private CapabilityAdmission? _installed;

    /// <summary>Initializes a new instance of the <see cref="CapabilityPublicationWorker"/> class.</summary>
    /// <param name="state">The host's state; this worker becomes its only writer.</param>
    /// <param name="source">The admission source.</param>
    /// <param name="cadence">The wait between attempts, and between currency checks while installed.</param>
    /// <param name="time">The clock the cadence is measured on.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cadence"/> is shorter than a millisecond or too long for a delay.</exception>
    public CapabilityPublicationWorker(PrimarySearchState state, ICapabilityAdmissionSource source, TimeSpan cadence, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cadence, MinCadence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cadence, MaxCadence);
        _state = state;
        _source = source;
        _cadence = cadence;
        _time = time;
    }

    /// <summary>Closes the publication path and withdraws the view, then cancels and joins the loop.</summary>
    /// <param name="cancellationToken">The host's stop bound; when it fires the join is abandoned, but nothing can install.</param>
    /// <returns>The join.</returns>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Close();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                CapabilityAdmission? installed;
                lock (_lifecycle)
                {
                    installed = _installed;
                }

                if (installed is null || !installed.IsCurrent())
                {
                    if (!TryClearForProbe())
                    {
                        return;
                    }

                    await AdmitAsync(stoppingToken);
                }

                await Task.Delay(_cadence, _time, stoppingToken);
            }
        }
        finally
        {
            Close();
        }
    }

    private async Task AdmitAsync(CancellationToken stoppingToken)
    {
        CapabilityAdmission? admission = await _source.TryAdmitAsync(stoppingToken);
        stoppingToken.ThrowIfCancellationRequested();
        if (admission is null)
        {
            return;
        }

        PrimarySearchState.Ready ready = _state.Prepare(admission.Capabilities, admission.IsCurrent, admission.ReadableResourceTypes);
        admission.TryCommitIfCurrent(() =>
        {
            lock (_lifecycle)
            {
                if (!_closed)
                {
                    _state.Install(ready);
                    _installed = admission;
                }
            }
        });
    }

    /// <summary>
    /// Unless closed, withdraws whatever the state holds (a stale admission, or a registered seed on the first attempt)
    /// so the host is cold while the source is probed; this worker is the state's only writer.
    /// </summary>
    /// <returns><see langword="false"/> when closed: start no probe. A close after this returns cancels the probe's token.</returns>
    private bool TryClearForProbe()
    {
        lock (_lifecycle)
        {
            if (_closed)
            {
                return false;
            }

            _installed = null;
            _state.Publish(null);
            return true;
        }
    }

    private void Close()
    {
        lock (_lifecycle)
        {
            _closed = true;
            _installed = null;
            _state.Publish(null);
        }
    }
}
