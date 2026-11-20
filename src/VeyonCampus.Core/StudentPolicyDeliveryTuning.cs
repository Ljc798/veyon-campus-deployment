namespace VeyonCampus.Core;

/// <summary>
/// Delivery limits shared by every channel that pushes a policy to a classroom.
/// <para>
/// The deadline must cover the agent writing the policy before it answers - at six seconds a machine
/// that was still applying the previous policy answered too late, the push was reported as failed, and
/// the retry then hit the revision check as "not newer", which is what looked like "sometimes it works".
/// </para>
/// <para>
/// Concurrency must stay low enough that one classroom switch is not saturated, but a room of
/// twenty-one machines has to finish in two waves rather than three: at eight it needed three, and the
/// whole push felt slower than the failure-prone version it replaced.
/// </para>
/// </summary>
internal static class StudentPolicyDeliveryTuning
{
    public static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(20);

    public const int PushParallelism = 12;
}
