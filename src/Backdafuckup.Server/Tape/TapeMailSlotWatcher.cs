using System.Collections.Concurrent;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Tape;

/// <summary>
/// Looks into the mail slots of every library (every 30 s by default), so that the console can offer to import what an
/// operator put in. It never moves anything, and skips a library whose robot is busy.
/// </summary>
public sealed class TapeMailSlotWatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    TapeLibraries libraries,
    TapeLocks locks,
    IOptions<TapeOptions> options,
    TimeProvider time,
    ILogger<TapeMailSlotWatcher> logger) : BackgroundService
{
    /// <summary>The last failure per library, so that an unreachable library is logged once, not every 30 s.</summary>
    private readonly ConcurrentDictionary<Guid, string> _failures = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.MailSlotPoll;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking the mail slots of the tape libraries failed.");
            }
        }
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        List<TapeLibraryEntity> all;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            all = await db.TapeLibraries.AsNoTracking().Include(l => l.Drives).Where(l => l.ChangerSerial != null && l.MailSlots > 0)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var library in all)
        {
            var robot = locks.Robot(library.Id).TryAcquire("Mail slot check");
            if (robot is null)
            {
                continue;
            }

            await using (robot.ConfigureAwait(false))
            {
                try
                {
                    await using var changer = await libraries.OpenChangerAsync(library, cancellationToken).ConfigureAwait(false);
                    var mail = await changer.Changer.ReadMailSlotsAsync(cancellationToken).ConfigureAwait(false);
                    if (await libraries.ApplyMailSlotsAsync(library.Id, mail, cancellationToken).ConfigureAwait(false))
                    {
                        logger.LogInformation("Mail slots of tape library {Library}: {Cartridges}", library.Name, string.Join(", ", mail.Where(m => m.Full).Select(m => m.VolumeTag?.Trim() ?? "(no barcode)")));
                    }

                    _failures.TryRemove(library.Id, out _);
                }
                catch (IOException ex)
                {
                    if (_failures.TryGetValue(library.Id, out var last) && last == ex.Message)
                    {
                        continue;
                    }

                    _failures[library.Id] = ex.Message;
                    logger.LogWarning("The mail slots of tape library {Library} could not be read: {Error}", library.Name, ex.Message);
                    await libraries.RecordErrorAsync(library.Id, $"The mail slots could not be read: {ex.Message}", cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
