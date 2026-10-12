using System.Collections.Immutable;
using System.Globalization;

namespace VeyonCampus.Core;

public enum ClassroomQuizState
{
    Open,
    Closed,
    Revealed
}

public enum ClassroomQuizSubmissionStatus
{
    Accepted,
    Duplicate,
    Closed,
    Expired,
    SubmissionLimitReached
}

public sealed record ClassroomQuizChoice(string Id, string Text);

public sealed record ClassroomQuizOptionResult(string Id, string Text, int Votes);

/// <summary>
/// Teacher-only, aggregate view of the single in-memory quiz for an active classroom session.
/// Per-target answers are never included in this contract.
/// </summary>
public sealed record ClassroomQuizTeacherSnapshot(
    int SchemaVersion,
    Guid SessionId,
    Guid QuizId,
    ClassroomQuizState State,
    string Question,
    ImmutableArray<ClassroomQuizChoice> Choices,
    int TargetCount,
    int RespondedCount,
    DateTimeOffset OpenedUtc,
    DateTimeOffset ExpiresUtc,
    ImmutableArray<ClassroomQuizOptionResult> Results);

/// <summary>A student view includes only that authenticated target's own selection.</summary>
public sealed record ClassroomQuizStudentSnapshot(
    int SchemaVersion,
    Guid SessionId,
    Guid QuizId,
    ClassroomQuizState State,
    string Question,
    ImmutableArray<ClassroomQuizChoice> Choices,
    string? SelectedChoiceId,
    bool CanSubmit,
    DateTimeOffset ExpiresUtc);

/// <summary>A target-scoped quiz page returned through its authenticated classroom grant.</summary>
public sealed record ClassroomQuizStudentPage(
    bool Ready,
    string? SignedQuestion,
    ClassroomQuizState? State,
    string? SelectedChoiceId,
    bool CanSubmit,
    DateTimeOffset? ExpiresUtc);

public sealed record ClassroomQuizAnswerReceipt(
    bool Accepted,
    ClassroomQuizSubmissionStatus Status,
    Guid QuizId,
    Guid SubmissionId);

/// <summary>
/// Bounded process-memory state for one active classroom quiz. The caller must supply a target
/// resolved from the authenticated Agent grant; this type never accepts a target from a quiz body.
/// Quiz state is deliberately not serialized or persisted.
/// </summary>
public sealed class ClassroomQuizStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumChoices = 2;
    public const int MaximumChoices = 5;
    public const int MaximumQuestionCharacters = 200;
    public const int MaximumChoiceCharacters = 80;
    public const int MaximumTargets = ClassroomSession.MaximumTargets;
    public const int MaximumSubmissionsPerTarget = 256;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(4);

    private readonly object _gate = new();
    private ActiveQuiz? _active;

    /// <summary>Opens a single-choice quiz for exactly the active session's target set.</summary>
    public ClassroomQuizTeacherSnapshot Open(ClassroomSession session, string question,
        IReadOnlyList<string> choiceTexts, DateTimeOffset openedUtc, DateTimeOffset expiresUtc)
    {
        ClassroomSession.Validate(session);
        ValidateUtc(openedUtc, nameof(openedUtc));
        ValidateUtc(expiresUtc, nameof(expiresUtc));
        if (session.Status != ClassroomSessionStatus.Active || session.EndedAtUtc is not null)
            throw new InvalidDataException("只能在进行中的课堂发起测验。");
        if (session.Targets.Length is < 1 or > MaximumTargets)
            throw new InvalidDataException($"课堂测验目标数量必须为 1–{MaximumTargets} 台。");
        if (expiresUtc <= openedUtc || expiresUtc - openedUtc > MaximumLifetime)
            throw new InvalidDataException("课堂测验有效期无效；最长为 4 小时。");

        var normalizedQuestion = ValidateText(question, MaximumQuestionCharacters, "题目");
        var choices = CreateChoices(choiceTexts);
        var targets = session.Targets
            .Select(target => ClassroomEventCryptography.NormalizeSingleTarget(target.DeviceLabel))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count != session.Targets.Length)
            throw new InvalidDataException("课堂目标电脑编号重复，无法发起测验。");

        lock (_gate)
        {
            if (_active is { } expired && openedUtc >= expired.ExpiresUtc)
                _active = null;
            if (_active is { State: ClassroomQuizState.Open or ClassroomQuizState.Closed })
                throw new InvalidDataException("当前测验尚未揭晓或取消；请先结束当前测验。");

            _active = new ActiveQuiz(session.SessionId, Guid.NewGuid(), normalizedQuestion, choices,
                targets, openedUtc, expiresUtc);
            return CreateTeacherSnapshot(_active);
        }
    }

    public ClassroomQuizTeacherSnapshot? ReadForTeacher(Guid sessionId, Guid quizId, DateTimeOffset nowUtc)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        lock (_gate)
        {
            var quiz = Find(sessionId, quizId);
            if (quiz is null) return null;
            if (ExpireIfNeeded(quiz, nowUtc)) return null;
            return CreateTeacherSnapshot(quiz);
        }
    }

    public ClassroomQuizTeacherSnapshot? ReadCurrentForTeacher(Guid sessionId, DateTimeOffset nowUtc)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        lock (_gate)
        {
            if (_active is not { } quiz || quiz.SessionId != sessionId)
                return null;
            if (ExpireIfNeeded(quiz, nowUtc)) return null;
            return CreateTeacherSnapshot(quiz);
        }
    }

    /// <summary>Reads a student's own answer using an identity supplied by the Agent grant.</summary>
    public ClassroomQuizStudentSnapshot? ReadForStudent(Guid sessionId, Guid quizId,
        string authenticatedTarget, DateTimeOffset nowUtc)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        var target = ClassroomEventCryptography.NormalizeSingleTarget(authenticatedTarget);
        lock (_gate)
        {
            var quiz = Find(sessionId, quizId);
            if (quiz is null) return null;
            if (ExpireIfNeeded(quiz, nowUtc)) return null;
            if (!quiz.Targets.Contains(target))
                throw new InvalidDataException("当前课堂测验未授权此目标电脑。");

            quiz.Selections.TryGetValue(target, out var selectedChoiceId);
            return new ClassroomQuizStudentSnapshot(CurrentSchemaVersion, quiz.SessionId, quiz.QuizId,
                quiz.State, quiz.Question, quiz.Choices, selectedChoiceId,
                quiz.State == ClassroomQuizState.Open && nowUtc < quiz.ExpiresUtc, quiz.ExpiresUtc);
        }
    }

    /// <summary>
    /// Accepts a vote for the authenticated target. Reusing a submission ID is idempotent;
    /// changing a choice replaces that target's previous vote.
    /// </summary>
    public ClassroomQuizSubmissionStatus Submit(Guid sessionId, Guid quizId, string authenticatedTarget,
        Guid submissionId, string choiceId, DateTimeOffset submittedUtc)
    {
        ValidateUtc(submittedUtc, nameof(submittedUtc));
        if (sessionId == Guid.Empty || quizId == Guid.Empty || submissionId == Guid.Empty)
            throw new InvalidDataException("课堂测验或提交 ID 无效。");
        var target = ClassroomEventCryptography.NormalizeSingleTarget(authenticatedTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(choiceId);

        lock (_gate)
        {
            var quiz = Find(sessionId, quizId);
            if (quiz is null) return ClassroomQuizSubmissionStatus.Expired;
            if (ExpireIfNeeded(quiz, submittedUtc)) return ClassroomQuizSubmissionStatus.Expired;
            if (!quiz.Targets.Contains(target))
                throw new InvalidDataException("当前课堂测验未授权此目标电脑。");
            if (quiz.State != ClassroomQuizState.Open) return ClassroomQuizSubmissionStatus.Closed;
            if (!quiz.Counts.ContainsKey(choiceId))
                throw new InvalidDataException("课堂测验选项无效。");

            if (quiz.Submissions.TryGetValue(submissionId, out var previousSubmission))
            {
                if (!string.Equals(previousSubmission.Target, target, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(previousSubmission.ChoiceId, choiceId, StringComparison.Ordinal))
                    throw new InvalidDataException("课堂测验提交 ID 已用于其他答案。");
                return ClassroomQuizSubmissionStatus.Duplicate;
            }

            if (!quiz.SubmissionIdsByTarget.TryGetValue(target, out var submissionIds))
            {
                submissionIds = [];
                quiz.SubmissionIdsByTarget.Add(target, submissionIds);
            }
            if (submissionIds.Count >= MaximumSubmissionsPerTarget)
                return ClassroomQuizSubmissionStatus.SubmissionLimitReached;

            submissionIds.Add(submissionId);
            quiz.Submissions.Add(submissionId, new SubmissionIdentity(target, choiceId));

            if (quiz.Selections.TryGetValue(target, out var previousChoiceId))
            {
                if (string.Equals(previousChoiceId, choiceId, StringComparison.Ordinal))
                    return ClassroomQuizSubmissionStatus.Duplicate;
                quiz.Counts[previousChoiceId]--;
            }
            else
            {
                quiz.RespondedCount++;
            }

            quiz.Selections[target] = choiceId;
            quiz.Counts[choiceId]++;
            return ClassroomQuizSubmissionStatus.Accepted;
        }
    }

    /// <summary>Stops new submissions and discards target-to-answer mappings.</summary>
    public ClassroomQuizTeacherSnapshot? Close(Guid sessionId, Guid quizId, DateTimeOffset nowUtc)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        lock (_gate)
        {
            var quiz = Find(sessionId, quizId);
            if (quiz is null) return null;
            if (ExpireIfNeeded(quiz, nowUtc)) return null;
            if (quiz.State == ClassroomQuizState.Open)
            {
                quiz.RespondedCount = quiz.Selections.Count;
                ClearTargetAnswers(quiz);
                quiz.State = ClassroomQuizState.Closed;
            }
            return CreateTeacherSnapshot(quiz);
        }
    }

    /// <summary>Releases only aggregate counts to teacher and mobile readers.</summary>
    public ClassroomQuizTeacherSnapshot? Reveal(Guid sessionId, Guid quizId, DateTimeOffset nowUtc)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        lock (_gate)
        {
            var quiz = Find(sessionId, quizId);
            if (quiz is null) return null;
            if (ExpireIfNeeded(quiz, nowUtc)) return null;
            if (quiz.State == ClassroomQuizState.Open)
                throw new InvalidDataException("请先关闭测验，再揭晓汇总结果。");
            quiz.State = ClassroomQuizState.Revealed;
            return CreateTeacherSnapshot(quiz);
        }
    }

    /// <summary>Cancellation immediately removes the question, answers and aggregate data.</summary>
    public bool Cancel(Guid sessionId, Guid quizId)
    {
        lock (_gate)
        {
            if (Find(sessionId, quizId) is null) return false;
            _active = null;
            return true;
        }
    }

    /// <summary>Ending a session invalidates its current quiz and all in-memory answer data.</summary>
    public bool EndSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (_gate)
        {
            if (_active?.SessionId != sessionId) return false;
            _active = null;
            return true;
        }
    }

    private ActiveQuiz? Find(Guid sessionId, Guid quizId) =>
        sessionId != Guid.Empty && quizId != Guid.Empty &&
        _active is { } quiz && quiz.SessionId == sessionId && quiz.QuizId == quizId
            ? quiz
            : null;

    private bool ExpireIfNeeded(ActiveQuiz quiz, DateTimeOffset nowUtc)
    {
        if (nowUtc < quiz.ExpiresUtc) return false;
        if (ReferenceEquals(_active, quiz)) _active = null;
        return true;
    }

    private static ClassroomQuizTeacherSnapshot CreateTeacherSnapshot(ActiveQuiz quiz)
    {
        var results = quiz.State == ClassroomQuizState.Revealed
            ? quiz.Choices.Select(choice =>
                new ClassroomQuizOptionResult(choice.Id, choice.Text, quiz.Counts[choice.Id])).ToImmutableArray()
            : ImmutableArray<ClassroomQuizOptionResult>.Empty;
        return new ClassroomQuizTeacherSnapshot(CurrentSchemaVersion, quiz.SessionId, quiz.QuizId,
            quiz.State, quiz.Question, quiz.Choices, quiz.Targets.Count, quiz.RespondedCount,
            quiz.OpenedUtc, quiz.ExpiresUtc, results);
    }

    internal static ImmutableArray<ClassroomQuizChoice> CreateChoices(IReadOnlyList<string> choiceTexts)
    {
        ArgumentNullException.ThrowIfNull(choiceTexts);
        if (choiceTexts.Count is < MinimumChoices or > MaximumChoices)
            throw new InvalidDataException($"课堂测验必须包含 {MinimumChoices}–{MaximumChoices} 个选项。");

        var choices = ImmutableArray.CreateBuilder<ClassroomQuizChoice>(choiceTexts.Count);
        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < choiceTexts.Count; index++)
        {
            var text = ValidateText(choiceTexts[index], MaximumChoiceCharacters, "选项");
            if (!seenTexts.Add(text))
                throw new InvalidDataException("课堂测验选项不能重复。");
            var choiceId = ((char)('A' + index)).ToString(CultureInfo.InvariantCulture);
            choices.Add(new ClassroomQuizChoice(choiceId, text));
        }
        return choices.MoveToImmutable();
    }

    internal static string ValidateText(string? value, int maximumCharacters, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"课堂测验{label}不能为空。");
        var normalized = value.Trim();
        if (normalized.Length > maximumCharacters || normalized.Any(character =>
                char.IsControl(character) || char.GetUnicodeCategory(character) is
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            throw new InvalidDataException($"课堂测验{label}不能超过 {maximumCharacters} 字，且不能包含控制字符。");
        return normalized;
    }

    private static void ValidateUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new InvalidDataException($"课堂测验时间 {name} 必须使用 UTC。");
    }

    private static void ClearTargetAnswers(ActiveQuiz quiz)
    {
        quiz.Selections.Clear();
        quiz.Submissions.Clear();
        quiz.SubmissionIdsByTarget.Clear();
    }

    private sealed class ActiveQuiz(Guid sessionId, Guid quizId, string question,
        ImmutableArray<ClassroomQuizChoice> choices, HashSet<string> targets,
        DateTimeOffset openedUtc, DateTimeOffset expiresUtc)
    {
        public Guid SessionId { get; } = sessionId;
        public Guid QuizId { get; } = quizId;
        public string Question { get; } = question;
        public ImmutableArray<ClassroomQuizChoice> Choices { get; } = choices;
        public HashSet<string> Targets { get; } = targets;
        public DateTimeOffset OpenedUtc { get; } = openedUtc;
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public ClassroomQuizState State { get; set; } = ClassroomQuizState.Open;
        public int RespondedCount { get; set; }
        public Dictionary<string, string> Selections { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Counts { get; } = choices.ToDictionary(
            choice => choice.Id, _ => 0, StringComparer.Ordinal);
        public Dictionary<Guid, SubmissionIdentity> Submissions { get; } = [];
        public Dictionary<string, HashSet<Guid>> SubmissionIdsByTarget { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SubmissionIdentity(string Target, string ChoiceId);
}
