namespace CloseMeansExit.Core;

/// <summary>
/// One bounded close attempt. A caller must re-probe identity and windows at the
/// actual termination boundary; this policy never grants a reusable kill permit.
/// </summary>
public sealed class CloseSession
{
    public static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan HiddenStability = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    private DateTimeOffset? _hiddenSince;
    private DateTimeOffset? _lastAdvanced;
    private bool _finished;

    public CloseObservation Observation { get; }
    public AppRule? CapturedRule { get; }

    public CloseSession(CloseObservation observation, AppRule? capturedRule)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Observation = observation;
        CapturedRule = capturedRule;
    }

    public PolicyDecision Advance(
        ProcessSnapshot snapshot,
        AppRule? currentRule,
        DateTimeOffset now,
        bool paused)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_finished)
        {
            return new(DecisionKind.Ignore, "此次关闭事件已经处理完毕。");
        }

        if (now < Observation.At || (_lastAdvanced is { } last && now < last))
        {
            return Finish(DecisionKind.Ignore, "时钟发生回退，取消此次操作。");
        }

        _lastAdvanced = now;

        if (paused)
        {
            return Finish(DecisionKind.Ignore, "监测已暂停。");
        }

        if (!snapshot.Reliable || !snapshot.IdentityMatches)
        {
            return Finish(DecisionKind.Ignore, snapshot.Diagnostic ?? "无法确认原进程身份或窗口状态。");
        }

        if (!snapshot.IsAlive)
        {
            return Finish(DecisionKind.Ignore, "应用已经自行退出。");
        }

        if (now - Observation.At >= Lifetime)
        {
            return Finish(DecisionKind.Ignore, "此次关闭事件已过期。");
        }

        bool armedAtClick = MatchesEnabled(CapturedRule);

        if (Observation.Evidence == CloseEvidence.BlacklistedBackground && !armedAtClick)
        {
            return Finish(DecisionKind.Ignore, "后台自动结束需要已启用且路径匹配的黑名单规则。");
        }

        if (armedAtClick && (!MatchesEnabled(currentRule) ||
            currentRule!.IncludeChildren != CapturedRule!.IncludeChildren ||
            currentRule.UseCustomCloseZone != CapturedRule.UseCustomCloseZone))
        {
            return Finish(DecisionKind.Ignore, "黑名单规则已移除、停用或更改，取消此次操作。");
        }

        if (Observation.Evidence == CloseEvidence.CustomTitleBarZone && armedAtClick &&
            (!CapturedRule!.UseCustomCloseZone || !currentRule!.UseCustomCloseZone))
        {
            return Finish(DecisionKind.Ignore, "当前规则未允许自绘标题栏兼容检测。");
        }

        if (snapshot.HasVisibleWindows)
        {
            if (Observation.Evidence == CloseEvidence.BlacklistedBackground)
            {
                return Finish(DecisionKind.Ignore, "应用有窗口或保存提示，取消本次后台自动结束。");
            }

            if (_hiddenSince.HasValue)
            {
                return Finish(DecisionKind.Ignore, "应用窗口或保存提示重新出现。");
            }

            if (now - Observation.At >= ExitGrace &&
                (Observation.Evidence == CloseEvidence.WindowDisappeared ||
                 Observation.SourceWindow is null || !snapshot.OnlySourceWindowVisible))
            {
                return Finish(DecisionKind.Ignore, "应用仍有窗口或保存提示，不执行结束。");
            }

            return new(DecisionKind.Wait, "等待应用处理关闭请求。");
        }

        _hiddenSince ??= now;

        if (now - Observation.At < ExitGrace || now - _hiddenSince.Value < HiddenStability)
        {
            return new(DecisionKind.Wait, "等待退出宽限期及窗口隐藏稳定期。");
        }

        if (!armedAtClick || Observation.Evidence == CloseEvidence.WindowDisappeared)
        {
            return Finish(DecisionKind.Candidate,
                Observation.Evidence == CloseEvidence.WindowDisappeared
                    ? "窗口消失后进程仍在运行；此证据仅作疑似候选。"
                    : "点击关闭后进程仍在运行，可由用户加入黑名单。");
        }

        return Finish(DecisionKind.Terminate, Observation.Evidence == CloseEvidence.BlacklistedBackground
            ? "已确认黑名单及持续无可见窗口的后台进程。"
            : "已确认关闭操作、黑名单及持续隐藏的原进程。");
    }

    private bool MatchesEnabled(AppRule? rule) =>
        rule is { Enabled: true } &&
        RulePaths.Equal(rule.ExecutablePath, Observation.Process.ExecutablePath);

    private PolicyDecision Finish(DecisionKind kind, string reason)
    {
        _finished = true;
        return new(kind, reason);
    }
}
