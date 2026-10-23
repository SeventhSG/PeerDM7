using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using PeerDM7.Core;
using PeerDM7.ViewModels;

// End-to-end smoke test: two real sessions in one process, talking over the real UDP stack.
// Run: dotnet run --project tests/PeerDM7.Smoke
// Exit code 0 means every check passed.

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

var thread = new Thread(() =>
{
    var dispatcher = Dispatcher.CurrentDispatcher;
    dispatcher.InvokeAsync(async () =>
    {
        try { await Run(); }
        catch (Exception ex) { Check(false, "unexpected exception: " + ex); }
        finally { dispatcher.InvokeShutdown(); }
    });
    Dispatcher.Run();
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

async Task<bool> Until(Func<bool> condition, int seconds)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end)
    {
        if (condition()) return true;
        await Task.Delay(100);
    }
    return condition();
}

async Task Run()
{
    using var dave = new MainViewModel("Dave");
    using var bora = new MainViewModel("Bora");
    dave.Start();
    bora.Start();

    Check(await Until(() => dave.Contacts.Any(c => c.IsOnline && c.Name == "Bora") && bora.Contacts.Any(c => c.IsOnline && c.Name == "Dave"), 20),
        "discovery: both sides see each other with names");

    var boraOnDave = dave.Contacts.First(c => c.Name == "Bora");
    var daveOnBora = bora.Contacts.First(c => c.Name == "Dave");
    var dConv = dave.ConversationFor(boraOnDave);
    var bConv = bora.ConversationFor(daveOnBora);

    // Messages with emoticon codes travel as text.
    dConv.Draft = "Hey Bora :) (H)";
    dConv.SendCommand.Execute(null);
    Check(await Until(() => bConv.Messages.Any(m => m.Text == "Hey Bora :) (H)"), 5), "direct message arrives");
    Check(await Until(() => dConv.Messages.Last().Model.Pending.Count == 0, 5), "delivery receipt comes back");

    // Spam: a burst of 10 lets 5 through (the first message above used one token).
    var before = bConv.Messages.Count;
    for (var i = 0; i < 10; i++)
    {
        dConv.Draft = "burst " + i;
        dConv.SendCommand.Execute(null);
    }
    await Task.Delay(1500);
    var arrived = bConv.Messages.Count - before;
    Check(arrived is >= 4 and <= 5, $"send limit: {arrived} of 10 burst messages went out");
    Check(dConv.Notice.Contains("Slow down"), "sender sees the slow down notice");

    // Nudges: the second one inside 15 seconds is refused.
    await Task.Delay(1500);
    var nudgesBefore = bConv.Messages.Count(m => m.IsNudge);
    dave.SendNudge(dConv);
    dave.SendNudge(dConv);
    await Task.Delay(1500);
    Check(bConv.Messages.Count(m => m.IsNudge) - nudgesBefore == 1, "only one nudge gets through");

    // Display picture.
    dave.SetPicture(PeerDM7.Controls.BuddyView.RenderPicture(PeerDM7.Controls.BuddyView.Palette[3]));
    Check(await Until(() => daveOnBora.Picture is not null, 5), "display picture reaches the other side");

    // File transfer: 5 MB of random bytes, checked by hash.
    var src = Path.Combine(Path.GetTempPath(), "peerdm7-smoke-" + Guid.NewGuid().ToString("N")[..6] + ".bin");
    var data = new byte[5 * 1024 * 1024 + 12345];
    new Random(7).NextBytes(data);
    await File.WriteAllBytesAsync(src, data);
    await Task.Delay(2000);

    dave.SendFile(dConv, src);
    Check(await Until(() => bConv.Messages.Any(m => m.Transfer?.State == TransferState.Offered), 10), "file offer arrives");
    var incoming = bConv.Messages.Last(m => m.Transfer is not null).Transfer!;
    var started = DateTime.UtcNow;
    incoming.AcceptCommand.Execute(null);
    var done = await Until(() => incoming.State is TransferState.Done or TransferState.Failed, 90);
    var secs = (DateTime.UtcNow - started).TotalSeconds;
    Check(done && incoming.State == TransferState.Done, $"file transfer finishes ({incoming.State}, {secs:0.0} s, {data.Length / 1024.0 / 1024 / Math.Max(secs, 0.01):0.0} MB/s)");
    if (incoming.SavedPath is { } saved && File.Exists(saved))
    {
        Check(SHA256.HashData(await File.ReadAllBytesAsync(saved)).SequenceEqual(SHA256.HashData(data)), "received file is byte for byte identical");
        File.Delete(saved);
    }
    var outgoing = dConv.Messages.Last(m => m.Transfer is not null).Transfer!;
    Check(await Until(() => outgoing.State == TransferState.Done, 5), "sender sees the file as received");
    File.Delete(src);

    // Group chat with both of them.
    dave.CreateGroup("Lab 3", [boraOnDave.Id]);
    Check(await Until(() => bora.Items.OfType<GroupViewModel>().Any(g => g.Title == "Lab 3"), 5), "group appears for the other member");
}
