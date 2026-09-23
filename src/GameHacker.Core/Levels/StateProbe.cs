using GameHacker.Core.Admin;
using GameHacker.Core.Channels;

namespace GameHacker.Core.Levels;

/// <summary>
/// 按判定的要求去问客户机，问回一份 <see cref="StateSnapshot"/>。
/// </summary>
/// <remarks>
/// <para><b>它不带循环。</b> 这是这个类最重要的性质。判定需要什么由
/// <see cref="LevelRun.Wanted"/> 说，什么时候去问由外面的事件决定 ——
/// 玩家在某台机器上敲了一行、交换机上过了包、管理员查完一次岗。
/// 没人动，就一次也不问。</para>
/// <para><b>为什么不能改成定时轮询。</b> 每次探查都要在客户机上真的跑几条命令，
/// 那几条命令会出现在玩家的 <c>ps</c> 里、会占着串口、会在玩家自己看机器的时候
/// 跳出来。判定器应当在玩家做了事之后才出现一瞬间，而不是每三秒在他机器上
/// 踱一次步。</para>
/// <para><b>合并与去抖</b>交给调用方：一串按键会触发一串事件，
/// 但同一台机器同时只该有一次探查在飞。</para>
/// </remarks>
public static class StateProbe
{
    public static async Task<StateSnapshot> AskAsync(
        StateQuery query, ControlChannel channel, CancellationToken cancellationToken = default)
    {
        var snapshot = new StateSnapshot(query.Machine);

        if (query.Processes)
        {
            string output = await channel.ProbeAsync("ps", null, cancellationToken).ConfigureAwait(false);
            snapshot = snapshot with { Processes = ProcessTable.Parse(output) };
        }

        if (query.Forwarding)
        {
            string output = await channel.ProbeAsync("forward", null, cancellationToken).ConfigureAwait(false);
            snapshot = snapshot with { Forwarding = output.Trim() == "1" };
        }

        if (query.FileSha is { } sha)
        {
            string output = await channel.ProbeAsync("file", sha, cancellationToken).ConfigureAwait(false);
            snapshot = snapshot with { FileFound = output.Trim() == "1" };
        }

        return snapshot;
    }
}
