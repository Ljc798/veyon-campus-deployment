using VeyonCampus.Core;

namespace VeyonCampus.App;

internal sealed record MobileClassroomChatMessageView(Guid EventId, string Sender, string Message,
    DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, bool Delivered);

internal sealed record ClassroomChatTranscriptSnapshot(long Revision,
    IReadOnlyList<MobileClassroomChatMessageView> Messages);

/// <summary>Bounded in-memory conversation snapshots shared by the Teacher UI and paired mobile pages.</summary>
internal sealed class ClassroomChatTranscriptBuffer
{
    public const int MaximumMessagesPerConversation = 200;

    private readonly object _gate = new();
    private readonly Dictionary<(Guid SessionId, string Target), Conversation> _conversations = [];

    private sealed class Conversation
    {
        public List<Entry> Messages { get; } = [];
        public long Revision { get; set; }
        public TaskCompletionSource<bool> Changed { get; set; } = NewSignal();
    }

    private sealed record Entry(ClassroomEvent Event, bool Delivered);

    public void AppendMessage(ClassroomEvent classroomEvent)
    {
        ArgumentNullException.ThrowIfNull(classroomEvent);
        if (classroomEvent.Type != ClassroomEventType.ClassroomChatMessage ||
            classroomEvent.SessionId == Guid.Empty || classroomEvent.EventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(classroomEvent.Message))
            throw new InvalidDataException("课堂聊天记录无效。");

        var target = NormalizeTargetKey(classroomEvent.Target);
        lock (_gate)
        {
            var key = (classroomEvent.SessionId, target);
            if (!_conversations.TryGetValue(key, out var conversation))
            {
                conversation = new Conversation();
                _conversations.Add(key, conversation);
            }

            var existingIndex = conversation.Messages.FindIndex(item => item.Event.EventId == classroomEvent.EventId);
            if (existingIndex >= 0)
            {
                var existing = conversation.Messages[existingIndex];
                if (existing.Event.Sender != classroomEvent.Sender ||
                    !string.Equals(existing.Event.Target, classroomEvent.Target, StringComparison.OrdinalIgnoreCase) ||
                    existing.Event.Message != classroomEvent.Message ||
                    existing.Event.CorrelationId != classroomEvent.CorrelationId)
                    throw new InvalidDataException("重复聊天消息标识与已保存消息不匹配。");
                if (classroomEvent.ExpiresUtc <= existing.Event.ExpiresUtc) return;
                conversation.Messages[existingIndex] = new Entry(classroomEvent, Delivered: false);
            }
            else
            {
                conversation.Messages.Add(new Entry(classroomEvent, Delivered: false));
                if (conversation.Messages.Count > MaximumMessagesPerConversation)
                    conversation.Messages.RemoveAt(0);
            }

            SignalChanged(conversation);
        }
    }

    public void MarkDelivered(Guid sessionId, string target, Guid eventId)
    {
        if (sessionId == Guid.Empty || eventId == Guid.Empty) return;
        var normalizedTarget = NormalizeTargetKey(target);
        lock (_gate)
        {
            if (!_conversations.TryGetValue((sessionId, normalizedTarget), out var conversation)) return;
            var index = conversation.Messages.FindIndex(item => item.Event.EventId == eventId &&
                item.Event.Sender == ClassroomEventSender.Teacher);
            if (index < 0 || conversation.Messages[index].Delivered) return;
            conversation.Messages[index] = conversation.Messages[index] with { Delivered = true };
            SignalChanged(conversation);
        }
    }

    public async Task<ClassroomChatTranscriptSnapshot> WaitForChangesAsync(Guid sessionId, string target,
        long afterRevision, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || afterRevision < 0 || maximumWait <= TimeSpan.Zero ||
            maximumWait > TimeSpan.FromSeconds(25))
            throw new InvalidDataException("课堂聊天修订号或等待时长无效。");
        var normalizedTarget = NormalizeTargetKey(target);
        var deadline = DateTimeOffset.UtcNow + maximumWait;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task signalTask;
            lock (_gate)
            {
                var key = (sessionId, normalizedTarget);
                if (!_conversations.TryGetValue(key, out var conversation))
                {
                    conversation = new Conversation();
                    _conversations.Add(key, conversation);
                }
                if (conversation.Revision > afterRevision)
                    return CreateSnapshot(conversation);
                signalTask = conversation.Changed.Task;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return ReadSnapshot(sessionId, normalizedTarget);
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(remaining, waitCancellation.Token);
            var completed = await Task.WhenAny(signalTask, delay).ConfigureAwait(false);
            if (completed == delay)
            {
                await delay.ConfigureAwait(false);
                return ReadSnapshot(sessionId, normalizedTarget);
            }

            waitCancellation.Cancel();
            await signalTask.ConfigureAwait(false);
        }
    }

    public void ClearSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty) return;
        lock (_gate)
        {
            foreach (var key in _conversations.Keys.Where(key => key.SessionId == sessionId).ToArray())
            {
                var conversation = _conversations[key];
                _conversations.Remove(key);
                conversation.Changed.TrySetResult(true);
            }
        }
    }

    public void ClearTargets(Guid sessionId, IReadOnlySet<string> activeTargets)
    {
        if (sessionId == Guid.Empty) return;
        ArgumentNullException.ThrowIfNull(activeTargets);
        var normalizedTargets = activeTargets.Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var key in _conversations.Keys.Where(key => key.SessionId == sessionId &&
                         !normalizedTargets.Contains(key.Target)).ToArray())
            {
                var conversation = _conversations[key];
                _conversations.Remove(key);
                conversation.Changed.TrySetResult(true);
            }
        }
    }

    private ClassroomChatTranscriptSnapshot ReadSnapshot(Guid sessionId, string target)
    {
        lock (_gate)
            return _conversations.TryGetValue((sessionId, target), out var conversation)
                ? CreateSnapshot(conversation)
                : EmptySnapshot();
    }

    private static ClassroomChatTranscriptSnapshot CreateSnapshot(Conversation conversation) =>
        new(conversation.Revision, Array.AsReadOnly(conversation.Messages.Select(entry =>
            new MobileClassroomChatMessageView(entry.Event.EventId,
                entry.Event.Sender == ClassroomEventSender.Teacher ? "teacher" : "student",
                entry.Event.Message ?? "", entry.Event.IssuedUtc, entry.Event.ExpiresUtc,
                entry.Delivered)).ToArray()));

    private static ClassroomChatTranscriptSnapshot EmptySnapshot() =>
        new(0, Array.Empty<MobileClassroomChatMessageView>());

    private static string NormalizeTargetKey(string target) =>
        ClassroomEventCryptography.NormalizeSingleTarget(target).ToUpperInvariant();

    private static void SignalChanged(Conversation conversation)
    {
        checked { conversation.Revision++; }
        var previous = conversation.Changed;
        conversation.Changed = NewSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
