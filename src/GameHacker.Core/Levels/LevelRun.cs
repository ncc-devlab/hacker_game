using System.Net;
using GameHacker.Core.Net;

namespace GameHacker.Core.Levels;

/// <summary>
/// 一次关卡运行的进度：当前在第几步、什么时候推进。
/// </summary>
/// <remarks>
/// <para>只认<b>当前这一步</b>的检测条件。前面的步骤不会因为玩家后来的操作
/// 退回去，后面的步骤也不会被提前满足 —— 否则提示和玩家眼前的状态对不上。</para>
/// <para><b>线程。</b> 网络事件来自交换机的转发线程，事件也在那个线程上触发，
/// 游戏侧要自己倒回主线程。</para>
/// </remarks>
public sealed class LevelRun
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IPAddress[]> _ipsOf;
    private int _index;

    public LevelRun(LevelDefinition level)
    {
        Level = level;
        _ipsOf = level.Machines.ToDictionary(m => m.Name, m => m.Nics.Select(n => IPAddress.Parse(n.Ip)).ToArray());
    }

    public LevelDefinition Level { get; }

    /// <summary>当前这一步的序号；全部完成后等于步骤数。</summary>
    public int CurrentIndex { get { lock (_gate) return _index; } }

    public LevelStep? CurrentStep { get { lock (_gate) return _index < Level.Steps.Count ? Level.Steps[_index] : null; } }

    public bool IsComplete => CurrentStep is null;

    /// <summary>某一步完成了。参数是刚完成的那一步。</summary>
    public event Action<LevelStep>? StepCompleted;

    /// <summary>全部步骤完成。紧跟在最后一步的 <see cref="StepCompleted"/> 之后触发。</summary>
    public event Action? Completed;

    /// <summary>交换机上过了一帧。挂到 <see cref="PacketLog.PacketCaptured"/> 上。</summary>
    public void Observe(PacketRecord packet)
    {
        LevelStep? done;
        bool finished;
        lock (_gate)
        {
            if (_index >= Level.Steps.Count) return;
            var step = Level.Steps[_index];
            bool hit = step.Check switch
            {
                PingCheck ping => ping.Matches(packet, _ipsOf),
                _ => false,
            };
            if (!hit) return;
            done = step;
            _index++;
            finished = _index == Level.Steps.Count;
        }
        // 事件在锁外触发，订阅方回调里再读进度不会死锁
        StepCompleted?.Invoke(done);
        if (finished) Completed?.Invoke();
    }
}
