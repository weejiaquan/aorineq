using System.Text.Json;

namespace AorinEQ.Core;

/// <summary>Everything a repair displaced, written to disk BEFORE anything is displaced.
///
/// <see cref="Stage"/> is what makes an interrupted repair recoverable: it is
/// <see cref="Applying"/> from before the first write until after the verification passes, so a
/// crash, a power cut or a killed elevated helper leaves a file on disk that says, precisely,
/// "this endpoint was changed and here is what it looked like". A backup with no stage at all
/// would be indistinguishable from a completed one.</summary>
public sealed record EapoRepairBackup(
    string EndpointGuid,
    string Stage,
    DateTimeOffset TakenAt,
    RegValue[] FxValues,
    RegValue[]? ChildApoValues)
{
    /// <summary>Written, not yet verified. If this is what a later session finds, the repair did
    /// not finish and the endpoint is in an unknown state.</summary>
    public const string Applying = "applying";

    /// <summary>Written and verified. Kept so the user can undo it later.</summary>
    public const string Applied = "applied";

    public bool IsInterrupted => Stage == Applying;

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // Written whole and flushed to the device before the caller is allowed to touch the
        // registry: a backup still sitting in a write cache is not a backup.
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this);
            stream.Flush(flushToDisk: true);
        }
    }

    public static EapoRepairBackup? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var backup = JsonSerializer.Deserialize<EapoRepairBackup>(File.ReadAllText(path));
            // A backup that cannot describe what it displaced is worse than none: it would let an
            // undo write a partial state. Treat it as absent.
            return backup is null || string.IsNullOrEmpty(backup.EndpointGuid) || backup.FxValues is null
                ? null
                : backup;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>How a repair ended, in terms the UI can say out loud.</summary>
public enum EapoRepairOutcome
{
    /// <summary>The endpoint was already being processed; nothing was written.</summary>
    AlreadyActive,
    /// <summary>Written and verified.</summary>
    Repaired,
    /// <summary>Nothing was written, because this endpoint is not one this can safely handle.</summary>
    Refused,
    /// <summary>Written, verification failed, and the endpoint was put back exactly as it was.</summary>
    RevertedAfterFailure,
    /// <summary>Something went wrong that left the endpoint changed. The backup on disk is the
    /// way out, and the caller must say so rather than pretend.</summary>
    FailedAndNotReverted,
    /// <summary>An undo completed.</summary>
    Undone,
}

public sealed record EapoRepairResult(EapoRepairOutcome Outcome, string Message);

/// <summary>The repair itself: back up, write, restart audio, verify, and put everything back if
/// the verification does not hold.
///
/// This is the only code in the project that writes to somebody else's audio device, so the shape
/// is deliberately paranoid and the order is the whole argument:
///
/// <list type="number">
/// <item>REFUSE unless this endpoint is one whose original state can be written back exactly and
/// whose effect slots are empty (see <see cref="EapoEndpoint.WhyNotRepairable"/>). Refusing costs
/// the user a click; guessing can cost them their sound.</item>
/// <item>CAPTURE every value under the endpoint's FxProperties and Equalizer APO's record for it,
/// including "this value was not there", and flush it to disk marked
/// <see cref="EapoRepairBackup.Applying"/> BEFORE the first write.</item>
/// <item>WRITE — only the two keys, only for the one endpoint GUID.</item>
/// <item>RESTART the audio services, because an APO is bound when the endpoint is built.</item>
/// <item>VERIFY with the same detector the rest of the app uses, plus the caller's own check that
/// the endpoint is still there and still usable.</item>
/// <item>On any failure, REVERT from the captured state, restart again, and say so.</item>
/// </list>
///
/// Runs entirely inside one elevated process. Splitting it — writing elevated and verifying
/// unelevated, say — would put a UAC prompt between the write and the revert, and a user who
/// declines that second prompt is left in the half-written state this design exists to prevent.</summary>
public static class EapoRepair
{
    /// <summary>Where the backup lives: machine-wide, not per-user. The repair runs elevated and
    /// the undo reads it unelevated, and those two can be different accounts when a standard user
    /// types an administrator's credentials at the prompt — a per-user path would then write the
    /// backup into a profile the user who needs it cannot see.</summary>
    public static string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ApoPaths.StateFolderName, "eapo-repair-backup.json");

    /// <summary>Where the elevated helper leaves its verdict for the process that launched it.
    /// An exit code alone cannot carry "and here is the message to show", and the outcomes worth
    /// distinguishing (refused / repaired / reverted / left changed) all need one.</summary>
    public static string ResultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ApoPaths.StateFolderName, "eapo-repair-result.json");

    public static void SaveResult(EapoRepairResult result)
    {
        try
        {
            var dir = Path.GetDirectoryName(ResultPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ResultPath, JsonSerializer.Serialize(result));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reads and CONSUMES the helper's verdict — deleting it, so a stale result from an
    /// earlier run can never be shown as the outcome of this one.</summary>
    public static EapoRepairResult? TakeResult()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            var result = JsonSerializer.Deserialize<EapoRepairResult>(File.ReadAllText(ResultPath));
            File.Delete(ResultPath);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Why the automatic repair cannot be OFFERED for this endpoint, or null when it can.
    ///
    /// Unprivileged, so the button can be shown or hidden without a prompt. It is a preview, not a
    /// decision: <see cref="Repair"/> re-runs every one of these checks inside the elevated helper,
    /// because the default device can change between the click and the prompt being answered.</summary>
    public static string? WhyNotAvailable(string? endpointGuid)
    {
        if (string.IsNullOrEmpty(endpointGuid) || !Guid.TryParse(endpointGuid, out _))
            return "AorinEQ can't tell which playback device you're using.";
        if (EapoDetection.GetInstallPath() is not { } install)
            return "Equalizer APO isn't installed yet.";
        if (EapoEndpoint.ResolveClsids(install) is not { } clsids)
            return "Equalizer APO's audio components aren't registered on this PC.";
        return EapoEndpoint.WhyNotRepairable(EapoEndpoint.ReadFxProperties(endpointGuid), clsids);
    }

    /// <summary>What the repair will do, in the user's terms — the text of the confirmation. It
    /// names the device setting being changed, the audio interruption, and the undo, because this
    /// is the one action in the app that writes to a Windows audio device.</summary>
    public const string ConfirmationText =
        "AorinEQ will switch Equalizer APO back on for the playback device you're using now, the same "
        + "way Equalizer APO's own Configurator does.\n\n"
        + "• Windows asks for administrator permission, because this is a system audio setting.\n"
        + "• Your current settings for this device are saved first, and put back automatically if "
        + "anything goes wrong.\n"
        + "• Windows audio restarts, so sound stops for a couple of seconds and media players may "
        + "need to be restarted.\n"
        + "• Only the device you're listening through is touched, and you can undo it afterwards.\n\n"
        + "Repair it now?";

    /// <summary>Repairs one endpoint, or explains why it will not. <paramref name="restartAudio"/>
    /// returns true when the audio services came back; <paramref name="verifyEndpointUsable"/> is
    /// the caller's independent check that the device is still enumerable and still opens for
    /// playback — the part that cannot be answered from the registry we just wrote.</summary>
    public static EapoRepairResult Repair(
        string endpointGuid, Func<bool> restartAudio, Func<bool> verifyEndpointUsable)
    {
        if (!Guid.TryParse(endpointGuid, out _))
            return new(EapoRepairOutcome.Refused, "that isn't a playback device AorinEQ recognises.");

        if (EapoDetection.GetInstallPath() is not { } install)
            return new(EapoRepairOutcome.Refused,
                "Equalizer APO isn't installed, so there's nothing to switch on. Use the setup guide first.");

        if (EapoEndpoint.ResolveClsids(install) is not { } clsids)
            return new(EapoRepairOutcome.Refused,
                "Equalizer APO's audio components aren't registered on this PC — reinstalling Equalizer APO "
                + "will fix that. AorinEQ won't guess at them.");

        if (EapoDetection.IsActiveOnEndpoint(endpointGuid))
            return new(EapoRepairOutcome.AlreadyActive,
                "Equalizer APO is already switched on for this device.");

        var fxBefore = EapoEndpoint.ReadFxProperties(endpointGuid);
        if (EapoEndpoint.WhyNotRepairable(fxBefore, clsids) is { } refusal)
            return new(EapoRepairOutcome.Refused, refusal);

        var backup = new EapoRepairBackup(
            endpointGuid, EapoRepairBackup.Applying, DateTimeOffset.UtcNow,
            fxBefore.ToArray(), EapoEndpoint.ReadChildApos(endpointGuid)?.ToArray());
        try
        {
            backup.Save(BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(EapoRepairOutcome.Refused,
                "AorinEQ couldn't save a record of your current settings, so it won't change them. (" + ex.Message + ")");
        }

        try
        {
            EapoEndpoint.WriteChildApos(endpointGuid, EapoEndpoint.RepairChildApoValues());
            EapoEndpoint.WriteFxProperties(endpointGuid, EapoEndpoint.RepairFxValues(clsids));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
            or IOException or InvalidOperationException)
        {
            return RevertAfterFailure(backup, restartAudio,
                "AorinEQ couldn't change the device's settings (" + ex.Message + ")");
        }

        bool restarted = restartAudio();
        if (!restarted)
        {
            return RevertAfterFailure(backup, restartAudio,
                "the Windows audio service couldn't be restarted, so the change couldn't take effect");
        }

        if (!EapoDetection.IsActiveOnEndpoint(endpointGuid))
        {
            return RevertAfterFailure(backup, restartAudio,
                "Windows didn't pick the change up");
        }
        if (!verifyEndpointUsable())
        {
            return RevertAfterFailure(backup, restartAudio,
                "the playback device stopped responding after the change");
        }

        try
        {
            (backup with { Stage = EapoRepairBackup.Applied }).Save(BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The repair itself worked and is verified. All this loses is the ability to undo it
            // from the button, so it must not be reported as a failure — and must not trigger a
            // revert of something that is working.
            return new(EapoRepairOutcome.Repaired,
                "Equalizer APO is switched on for this device again. (AorinEQ couldn't keep an undo record: "
                + ex.Message + ")");
        }

        return new(EapoRepairOutcome.Repaired,
            "Equalizer APO is switched on for this device again.");
    }

    /// <summary>Puts everything back and says what happened. Used both by the automatic revert and
    /// by the user's own "Undo". Idempotent enough to run twice: restoring a captured state over
    /// itself is a no-op.</summary>
    public static EapoRepairResult Undo(EapoRepairBackup backup, Func<bool> restartAudio)
    {
        try
        {
            RestoreFrom(backup);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
            or IOException or InvalidOperationException)
        {
            return new(EapoRepairOutcome.FailedAndNotReverted,
                "AorinEQ couldn't put the device's settings back (" + ex.Message
                + "). Equalizer APO's own Configurator can reset this device.");
        }
        restartAudio();
        TryDeleteBackup();
        return new(EapoRepairOutcome.Undone,
            "The device's settings are back exactly as they were before the repair.");
    }

    private static EapoRepairResult RevertAfterFailure(
        EapoRepairBackup backup, Func<bool> restartAudio, string why)
    {
        try
        {
            RestoreFrom(backup);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
            or IOException or InvalidOperationException)
        {
            // The one outcome that must never be dressed up: the endpoint is changed and this
            // could not change it back. The backup stays on disk, and the caller tells the user.
            return new(EapoRepairOutcome.FailedAndNotReverted,
                "The repair failed because " + why + ", and AorinEQ could not undo its change ("
                + ex.Message + "). Use Equalizer APO's Configurator to reset this device.");
        }
        restartAudio();
        TryDeleteBackup();
        return new(EapoRepairOutcome.RevertedAfterFailure,
            "The repair didn't work — " + why + " — so AorinEQ put everything back exactly as it was. "
            + "Equalizer APO's own Configurator can switch the device on manually.");
    }

    private static void RestoreFrom(EapoRepairBackup backup)
    {
        EapoEndpoint.RestoreFxProperties(backup.EndpointGuid, backup.FxValues);
        if (backup.ChildApoValues is null)
            EapoEndpoint.DeleteChildApos(backup.EndpointGuid);
        else
            EapoEndpoint.RestoreChildApos(backup.EndpointGuid, backup.ChildApoValues);
    }

    public static void TryDeleteBackup()
    {
        try
        {
            File.Delete(BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
