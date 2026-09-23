using GameHacker.Core.Admin;
using GameHacker.Core.Net;

namespace GameHacker.Core.Levels;

/// <summary>
/// 一次关卡运行的进度：当前在第几步、什么时候推进。
/// </summary>
/// <remarks>
/// <para>只认<b>当前这一步</b>的检测条件。前面的步骤不会因为玩家后来的操作
/// 退回去，后面的步骤也不会被提前满足 —— 否则提示和玩家眼前的状态对不上。</para>
/// <para><b>三个消息来源</b>，对应三条判定通路：交换机上的帧、问回来的客户机状态、
/// 管理员查岗的结论。判定器自己不主动做任何事，包括不去轮询客户机 ——
/// 它只说「这一步想知道什么」（<see cref="Wanted"/>），由宿主在真的发生了事情
/// 之后去问一次。</para>
/// <para><b>线程。</b> 网络事件来自交换机的转发线程，管理员的结论来自他自己的
/// 后台线程，事件也就在那些线程上触发，游戏侧要自己倒回主线程。</para>
/// </remarks>
public sealed class LevelRun
{
    private readonly object _gate = new();
    private readonly LevelWorld _world;
    private int _index;
    private CheckTracker? _tracker;

    public LevelRun(LevelDefinition level)
    {
        Level = level;
        _world = new LevelWorld(level);
        _tracker = NewTracker();
    }

    public LevelDefinition Level { get; }

    /// <summary>当前这一步的序号；全部完成后等于步骤数。</summary>
    public int CurrentIndex { get { lock (_gate) return _index; } }

    public LevelStep? CurrentStep { get { lock (_gate) return _index < Level.Steps.Count ? Level.Steps[_index] : null; } }

    public bool IsComplete => CurrentStep is null;

    /// <summary>当前这一步还差什么。没什么好说的时候是空串。</summary>
    public string Remaining { get { lock (_gate) return _tracker?.Remaining ?? ""; } }

    /// <summary>
    /// 当前这一步要看客户机里的什么；不看就是 null。
    /// </summary>
    /// <remarks>
    /// 宿主拿到它之后，只在<b>可能改变这些状态的事情发生之后</b>问一次
    /// （玩家敲了一行命令、交换机上过了包、管理员查完岗），不要拿它当轮询的凭据。
    /// </remarks>
    public StateQuery? Wanted { get { lock (_gate) return CurrentStep?.Check?.Query; } }

    /// <summary>某一步完成了。参数是刚完成的那一步。</summary>
    public event Action<LevelStep>? StepCompleted;

    /// <summary>全部步骤完成。紧跟在最后一步的 <see cref="StepCompleted"/> 之后触发。</summary>
    public event Action? Completed;

    /// <summary>交换机上过了一帧。挂到 <see cref="PacketLog.PacketCaptured"/> 上。</summary>
    public void Observe(PacketRecord packet) => Advance(t => t.Observe(packet));

    /// <summary>问回来的客户机状态。</summary>
    public void Observe(StateSnapshot state) => Advance(t => t.Observe(state));

    /// <summary>管理员查完了一次岗。挂到 <see cref="AdminAgent.PatrolCompleted"/> 上。</summary>
    public void Observe(PatrolReport patrol) => Advance(t => t.Observe(patrol));

    private CheckTracker? NewTracker() =>
        _index < Level.Steps.Count ? Level.Steps[_index].Check?.CreateTracker(_world) : null;

    private void Advance(Func<CheckTracker, bool> hit)
    {
        LevelStep done;
        bool finished;
        lock (_gate)
        {
            if (_tracker is null || _index >= Level.Steps.Count) return;
            if (!hit(_tracker)) return;
            done = Level.Steps[_index];
            _index++;
            _tracker = NewTracker();
            finished = _index == Level.Steps.Count;
        }
        // 事件在锁外触发，订阅方回调里再读进度不会死锁
        StepCompleted?.Invoke(done);
        if (finished) Completed?.Invoke();
    }
}
