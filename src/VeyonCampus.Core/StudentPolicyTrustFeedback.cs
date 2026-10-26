using System.Net;

namespace VeyonCampus.Core;

internal static class StudentPolicyTrustFeedback
{
    public static string? ExplainRejection(HttpStatusCode statusCode, string responseBody, string operation)
    {
        if (statusCode != HttpStatusCode.Conflict ||
            !(responseBody.Contains("签名无效", StringComparison.OrdinalIgnoreCase) ||
              responseBody.Contains("签名不匹配", StringComparison.OrdinalIgnoreCase) ||
              responseBody.Contains("signature invalid", StringComparison.OrdinalIgnoreCase)))
            return null;

        return $"学生 Agent 拒绝了{operation}请求：学生端仍信任另一组教师策略公钥。若刚切换到 Mac 教师端，请在 Mac 的“首次设置向导 → 学生配置包”中选择原学生包，生成密钥更新包，再用最新 StudentSetup 以管理员身份在这台学生机重新应用。完成新签名状态读回前，此设备保持未信任；本次操作未计为成功。原策略会由学生端更新流程按既有恢复步骤处理。";
    }
}
