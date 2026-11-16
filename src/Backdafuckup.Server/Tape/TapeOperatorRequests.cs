using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Tape;

/// <summary>
/// Requests to the operator: a row the console shows on every page while a session waits, polling whether it was done.
/// Requests of sessions that ended (a restart) are resolved by the startup recovery.
/// </summary>
public sealed class TapeOperatorRequests(IDbContextFactory<AppDbContext> dbFactory, IOptions<TapeOptions> options, TimeProvider time)
{
    /// <summary>
    /// Asks the operator, then polls <paramref name="done"/> until it returns true. <paramref name="done"/> may change the
    /// message (a wrong cartridge: say so) through the callback it gets. Gives up after <see cref="TapeOptions.OperatorTimeout"/>.
    /// </summary>
    public async Task WaitAsync(Guid sessionId, Guid libraryId, Guid? driveId, string message, Func<Action<string>, CancellationToken, Task<bool>> done, CancellationToken cancellationToken)
    {
        Guid id;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var request = new TapeOperatorRequestEntity { SessionId = sessionId, LibraryId = libraryId, DriveId = driveId, Message = message, CreatedUtc = time.GetUtcNow() };
            db.TapeOperatorRequests.Add(request);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            id = request.Id;
        }

        try
        {
            var deadline = time.GetUtcNow() + options.Value.OperatorTimeout;
            while (true)
            {
                if (await done(text => Update(id, $"{text} {message}"), cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (time.GetUtcNow() > deadline)
                {
                    throw new TimeoutException($"Nobody did what was asked within {options.Value.OperatorTimeout.TotalHours:0} hours: {message}");
                }

                await Task.Delay(options.Value.OperatorPoll, time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await db.TapeOperatorRequests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ResolvedUtc, time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<TapeOperatorRequestDto>> PendingAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.TapeOperatorRequests.AsNoTracking().Where(r => r.ResolvedUtc == null).OrderBy(r => r.CreatedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        var sessionIds = pending.Select(r => r.SessionId).ToList();
        var sessions = await db.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken).ConfigureAwait(false);
        var libraries = await db.TapeLibraries.AsNoTracking().Include(l => l.Drives).ToDictionaryAsync(l => l.Id, cancellationToken).ConfigureAwait(false);
        return pending.Select(r => new TapeOperatorRequestDto(
            r.Id, r.SessionId, sessions.GetValueOrDefault(r.SessionId) ?? string.Empty, r.LibraryId, libraries.GetValueOrDefault(r.LibraryId)?.Name ?? "?",
            libraries.GetValueOrDefault(r.LibraryId)?.Drives.FirstOrDefault(d => d.Id == r.DriveId)?.Name, r.Message, r.CreatedUtc)).ToList();
    }

    private void Update(Guid id, string message)
    {
        using var db = dbFactory.CreateDbContext();
        db.TapeOperatorRequests.Where(r => r.Id == id).ExecuteUpdate(s => s.SetProperty(r => r.Message, message.Length > 1000 ? message[..1000] : message));
    }
}
