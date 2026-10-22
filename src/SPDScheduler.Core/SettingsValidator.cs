using System.Text.RegularExpressions;

namespace SPDScheduler.Core;

public static class SettingsValidator
{
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ResourcePolicyVersion != 1) throw new ArgumentException("지원하지 않는 리소스 판정 설정 버전입니다.");
        if (settings.PollIntervalMinutes is < 1 or > 60 || settings.IdleSeconds is < 10 or > 86400 || !double.IsFinite(settings.IdleCpuPercent) ||
            settings.IdleCpuPercent is <= 0 or > 100 || settings.IdleMemoryMiB is < 0 or > 1048576 ||
            settings.IdleIoKiBPerSecond is < 0 or > 1048576 || settings.StartTimeoutMinutes is < 1 or > 1440)
            throw new ArgumentException("확인 간격(1~60분), 유휴 시간(10~86400초), CPU(0 초과~100%), 메모리/I/O(0 이상), 시작 대기(1~1440분)를 확인하세요.");
        if (settings.CompletionRules is null) throw new ArgumentException("완료 규칙 목록이 없습니다.");
        foreach (var rule in settings.CompletionRules)
        {
            if (rule is not null && !Enum.IsDefined(rule.Tool))
                throw new ArgumentException("완료 규칙의 시뮬레이션 도구가 유효하지 않습니다.");
            if (rule is null || string.IsNullOrWhiteSpace(rule.Workflow) || string.IsNullOrWhiteSpace(rule.SpdPattern) || string.IsNullOrWhiteSpace(rule.StartedPattern) ||
                string.IsNullOrWhiteSpace(rule.CompletedPattern))
                throw new ArgumentException("완료 규칙에는 모드 이름, SPD 식별식, 시작/완료 표현식이 필요합니다.");
            foreach (var pattern in new[] { rule.SpdPattern, rule.StartedPattern, rule.CompletedPattern })
            {
                if (pattern.Length > 2048) throw new ArgumentException("표현식은 2048자 이하여야 합니다.");
                _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
        }
    }
}
