using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>A student seat at a physical grid coordinate, optionally bound to one Veyon computer.</summary>
public sealed record ClassroomSeatGridSeat(Guid SeatId, int Row, int Column, string StudentName,
    string? VeyonHostAddress = null, string? VeyonComputerName = null, Guid? VeyonObjectId = null,
    Guid? VeyonLocationObjectId = null, int? ComputerNumber = null);

/// <summary>A room-local seating grid. Computer numbers are saved on each physical seat and never derived from its position.</summary>
public sealed record ClassroomSeatGridLayout(Guid CampusProfileId, Guid RoomId, int Rows, int Columns,
    ClassroomSeatGridSeat[] Seats, bool ComputerNumbersInitialized = true);

/// <summary>A visible grid cell and its assigned computer number, if it has one.</summary>
public sealed record ClassroomSeatGridCell(int Row, int Column, int? SeatNumber,
    string? StudentName, string? VeyonHostAddress, string? VeyonComputerName, Guid? VeyonObjectId = null,
    Guid? VeyonLocationObjectId = null, Guid? SeatId = null)
{
    public bool IsSeat => SeatId.HasValue;
}

/// <summary>A seat summary used by classroom and mobile views.</summary>
public sealed record ClassroomSeatGridLocation(Guid SeatId, int? SeatNumber, int Row, int Column,
    string StudentName, string? VeyonHostAddress, string? VeyonComputerName, Guid? VeyonObjectId = null,
    Guid? VeyonLocationObjectId = null);

/// <summary>A Veyon target read back from the directory and matched to one physical seat.</summary>
public sealed record ClassroomSeatGridTargetBinding(Guid SeatId, string HostAddress, string ComputerName,
    Guid ObjectId, Guid LocationObjectId);

/// <summary>
/// Per-user local storage for editable classroom seating grids. It never stores a network request,
/// package, or student-device telemetry.
/// </summary>
public sealed class ClassroomSeatGridStore
{
    private sealed record SeatGridDocument(int SchemaVersion, List<ClassroomSeatGridLayout> Layouts);
    private sealed record LegacySeatLayout(int SchemaVersion, List<LegacySeatLayoutEntry> Layouts);
    private sealed record LegacySeatLayoutEntry(Guid CampusProfileId, Guid RoomId, int Columns,
        string[] TargetOrder);

    public const int CurrentSchemaVersion = 5;
    private const int PreviousSchemaVersion = 4;
    private const int OlderSupportedSchemaVersion = 3;
    private const int OldestSupportedSchemaVersion = 2;
    private const int LegacyDefaultRows = 25;
    private const int LegacyDefaultColumns = 25;
    public const int DefaultRows = 15;
    public const int DefaultColumns = 15;
    public const int MaximumEditableRows = 30;
    public const int MaximumEditableColumns = 30;
    public const int MaximumRows = 150;
    public const int MaximumColumns = 150;
    public const int MaximumCells = 10_000;
    public const int MaximumLayouts = 1000;
    public const int MaximumSeats = ClassroomSession.MaximumTargets;
    public const int MaximumFileBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private static readonly object ProcessGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly string _legacyPath;

    public ClassroomSeatGridStore(string? path = null, string? legacyPath = null)
    {
        _path = Path.GetFullPath(path ?? DefaultPath);
        var directory = Path.GetDirectoryName(_path)
                       ?? throw new ArgumentException("本机座位图路径无效。", nameof(path));
        _legacyPath = Path.GetFullPath(legacyPath ?? (path is null
            ? ClassroomSeatLayoutStore.DefaultPath
            : Path.Combine(directory, "classroom-seat-layouts.json")));
        if (string.Equals(_path, _legacyPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("新旧座位图文件必须使用不同路径。", nameof(legacyPath));
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "classroom-seat-grid.json");

    public ClassroomSeatGridLayout GetLayout(Guid campusProfileId, Guid roomId, int legacyComputerNumberStart = 1)
    {
        ValidateIdentity(campusProfileId, roomId);
        if (legacyComputerNumberStart is < 1 or > MachineNaming.MaximumNumber)
            throw new InvalidDataException($"旧版座位编号起点必须在 1–{MachineNaming.MaximumNumber} 之间。");
        lock (ProcessGate)
        {
            if (!File.Exists(_path))
            {
                if (!File.Exists(_legacyPath)) return CreateDefault(campusProfileId, roomId);
                using (AcquireFileLock())
                {
                    if (!File.Exists(_path)) WriteDocument(ReadLegacyDocument());
                }
            }
            using (AcquireFileLock())
            {
                var document = ReadDocument();
                var layout = document.Layouts.SingleOrDefault(item =>
                    item.CampusProfileId == campusProfileId && item.RoomId == roomId);
                if (layout is null) return CreateDefault(campusProfileId, roomId);
                if (!layout.ComputerNumbersInitialized)
                {
                    layout = InitializeLegacyComputerNumbers(layout, legacyComputerNumberStart);
                    WriteDocument(Replace(document, layout));
                }
                return Clone(layout);
            }
        }
    }

    public void DeleteLayout(Guid campusProfileId, Guid roomId)
    {
        ValidateIdentity(campusProfileId, roomId);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            if (!File.Exists(_path) && !File.Exists(_legacyPath)) return;
            var document = File.Exists(_path) ? ReadDocument() : ReadLegacyDocument();
            var remaining = document.Layouts.Where(layout =>
                layout.CampusProfileId != campusProfileId || layout.RoomId != roomId).ToList();
            if (remaining.Count == document.Layouts.Count) return;
            WriteDocument(new SeatGridDocument(CurrentSchemaVersion, remaining));
        }
    }

    public ClassroomSeatGridLayout SetDimensions(Guid campusProfileId, Guid roomId, int rows, int columns,
        bool removeSeatsOutsideGrid = false)
    {
        ValidateIdentity(campusProfileId, roomId);
        if (rows is < 1 or > MaximumEditableRows || columns is < 1 or > MaximumEditableColumns)
            throw new InvalidDataException($"可编辑座位图行数和列数必须在 1–{MaximumEditableRows} 之间。");
        return Update(campusProfileId, roomId, layout =>
        {
            var removedSeats = layout.Seats.Where(seat => seat.Row > rows || seat.Column > columns).ToArray();
            if (removedSeats.Length > 0 && !removeSeatsOutsideGrid)
                throw new InvalidDataException("缩小网格会移除范围外的座位；请确认后再应用。");
            return layout with
            {
                Rows = rows,
                Columns = columns,
                Seats = removedSeats.Length == 0
                    ? layout.Seats
                    : layout.Seats.Except(removedSeats).ToArray()
            };
        });
    }

    public ClassroomSeatGridLayout AddOrUpdateSeat(Guid campusProfileId, Guid roomId,
        int row, int column, string studentName)
    {
        ValidateIdentity(campusProfileId, roomId);
        ValidateStudentName(studentName);
        return Update(campusProfileId, roomId, layout =>
        {
            ValidateCoordinate(layout, row, column);
            var index = Array.FindIndex(layout.Seats, seat => seat.Row == row && seat.Column == column);
            if (index >= 0)
            {
                var seats = layout.Seats.ToArray();
                seats[index] = seats[index] with { StudentName = studentName };
                return layout with { Seats = seats };
            }
            if (layout.Seats.Length >= MaximumSeats)
                throw new InvalidDataException($"一个教室最多可添加 {MaximumSeats} 个座位。");
            return layout with
            {
                Seats = layout.Seats.Append(new ClassroomSeatGridSeat(Guid.NewGuid(), row, column, studentName))
                    .ToArray()
            };
        });
    }

    public ClassroomSeatGridLayout SaveSeat(Guid campusProfileId, Guid roomId,
        int row, int column, string studentName, int? computerNumber)
    {
        ValidateIdentity(campusProfileId, roomId);
        ValidateStudentName(studentName);
        if (computerNumber is { } number && number is < 1 or > MachineNaming.MaximumNumber)
            throw new InvalidDataException($"电脑编号请输入 1–{MachineNaming.MaximumNumber}。");
        return Update(campusProfileId, roomId, layout =>
        {
            ValidateCoordinate(layout, row, column);
            var index = Array.FindIndex(layout.Seats, seat => seat.Row == row && seat.Column == column);
            if (index < 0)
            {
                if (layout.Seats.Length >= MaximumSeats)
                    throw new InvalidDataException($"一个教室最多可添加 {MaximumSeats} 个座位。");
                if (computerNumber is { } newNumber && layout.Seats.Any(seat => seat.ComputerNumber == newNumber))
                    throw new InvalidDataException($"电脑编号 {newNumber} 已分配给其他座位。");
                return layout with
                {
                    Seats = layout.Seats.Append(new ClassroomSeatGridSeat(Guid.NewGuid(), row, column,
                        studentName, ComputerNumber: computerNumber)).ToArray()
                };
            }

            var existing = layout.Seats[index];
            if (existing.ComputerNumber != computerNumber &&
                (existing.VeyonObjectId is not null || !string.IsNullOrWhiteSpace(existing.VeyonHostAddress) ||
                 !string.IsNullOrWhiteSpace(existing.VeyonComputerName)))
                throw new InvalidDataException("此座位已关联 Veyon 电脑；为避免电脑身份错位，不能直接改号。");
            if (computerNumber is { } requested && layout.Seats.Where((_, seatIndex) => seatIndex != index)
                    .Any(seat => seat.ComputerNumber == requested))
                throw new InvalidDataException($"电脑编号 {computerNumber} 已分配给其他座位。");
            var seats = layout.Seats.ToArray();
            seats[index] = existing with { StudentName = studentName, ComputerNumber = computerNumber };
            return layout with { Seats = seats };
        });
    }

    public ClassroomSeatGridLayout SetComputerNumber(Guid campusProfileId, Guid roomId,
        int row, int column, int? computerNumber)
    {
        ValidateIdentity(campusProfileId, roomId);
        if (computerNumber is { } number && number is < 1 or > MachineNaming.MaximumNumber)
            throw new InvalidDataException($"电脑编号请输入 1–{MachineNaming.MaximumNumber}。");
        return Update(campusProfileId, roomId, layout =>
        {
            ValidateCoordinate(layout, row, column);
            var seatIndex = Array.FindIndex(layout.Seats, seat => seat.Row == row && seat.Column == column);
            if (seatIndex < 0) throw new InvalidDataException("请先添加座位，再设置电脑编号。");
            var seat = layout.Seats[seatIndex];
            if (seat.ComputerNumber == computerNumber) return layout;
            if (seat.VeyonObjectId is not null || !string.IsNullOrWhiteSpace(seat.VeyonHostAddress) ||
                !string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                throw new InvalidDataException("此座位已关联 Veyon 电脑；为避免电脑身份错位，不能直接改号。请先解除关联并确认电脑目录。");
            if (computerNumber is { } requested && layout.Seats.Where((_, index) => index != seatIndex)
                    .Any(item => item.ComputerNumber == requested))
                throw new InvalidDataException($"电脑编号 {computerNumber} 已分配给其他座位。");
            var seats = layout.Seats.ToArray();
            seats[seatIndex] = seat with { ComputerNumber = computerNumber };
            return layout with { Seats = seats };
        });
    }

    /// <summary>Assigns only currently unnumbered seats. Existing computer numbers never move.</summary>
    public ClassroomSeatGridLayout AssignNextComputerNumbers(Guid campusProfileId, Guid roomId,
        int firstNumber)
    {
        ValidateIdentity(campusProfileId, roomId);
        if (firstNumber is < 1 or > MachineNaming.MaximumNumber)
            throw new InvalidDataException($"电脑编号起点必须在 1–{MachineNaming.MaximumNumber} 之间。");
        return Update(campusProfileId, roomId, layout =>
        {
            var pending = layout.Seats.Where(seat => seat.ComputerNumber is null &&
                                                      !string.IsNullOrWhiteSpace(seat.StudentName))
                .OrderBy(seat => seat.Row).ThenBy(seat => seat.Column).ToArray();
            if (pending.Length == 0) return layout;

            var used = layout.Seats.Where(seat => seat.ComputerNumber is not null)
                .Select(seat => seat.ComputerNumber!.Value).ToHashSet();
            var next = Math.Max(firstNumber - 1, used.DefaultIfEmpty(firstNumber - 1).Max());
            var byId = new Dictionary<Guid, int>();
            foreach (var seat in pending)
            {
                do { next++; } while (used.Contains(next) && next <= MachineNaming.MaximumNumber);
                if (next > MachineNaming.MaximumNumber)
                    throw new InvalidDataException($"没有可分配的电脑编号；最大编号为 {MachineNaming.MaximumNumber}。");
                used.Add(next);
                byId.Add(seat.SeatId, next);
            }
            return layout with
            {
                Seats = layout.Seats.Select(seat => byId.TryGetValue(seat.SeatId, out var number)
                    ? seat with { ComputerNumber = number }
                    : seat).ToArray()
            };
        });
    }

    public ClassroomSeatGridLayout RemoveSeat(Guid campusProfileId, Guid roomId,
        int row, int column, bool unassignVeyonTarget = false)
    {
        ValidateIdentity(campusProfileId, roomId);
        return Update(campusProfileId, roomId, layout =>
        {
            ValidateCoordinate(layout, row, column);
            var seat = layout.Seats.SingleOrDefault(item => item.Row == row && item.Column == column);
            if (seat is null) return layout;
            if (!unassignVeyonTarget &&
                (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress) ||
                 !string.IsNullOrWhiteSpace(seat.VeyonComputerName)))
                throw new InvalidDataException("此座位仍关联或待关联 Veyon 电脑；请先明确解除电脑关联，再移除座位。");
            return layout with
            {
                Seats = layout.Seats.Where(item => item.SeatId != seat.SeatId).ToArray()
            };
        });
    }

    public ClassroomSeatGridLayout AssignVeyonTarget(Guid campusProfileId, Guid roomId,
        int row, int column, string hostAddress, string computerName, Guid objectId, Guid locationObjectId)
    {
        ValidateIdentity(campusProfileId, roomId);
        var normalizedHost = VeyonHostAddress.NormalizeOverride(hostAddress);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            throw new InvalidDataException("Veyon 电脑地址不能为空。");
        ValidateVeyonComputerName(computerName);
        if (objectId == Guid.Empty || locationObjectId == Guid.Empty)
            throw new InvalidDataException("Veyon 电脑或地点标识无效。");
        return Update(campusProfileId, roomId, layout =>
        {
            ValidateCoordinate(layout, row, column);
            var seatIndex = Array.FindIndex(layout.Seats, seat => seat.Row == row && seat.Column == column);
            if (seatIndex < 0) throw new InvalidDataException("请先选择一个座位，再关联 Veyon 电脑。");
            if (layout.Seats.Where((_, index) => index != seatIndex).Any(seat =>
                    string.Equals(seat.VeyonHostAddress, normalizedHost, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("这台 Veyon 电脑已经关联到其他座位。");
            if (layout.Seats.Where((_, index) => index != seatIndex)
                .Any(seat => seat.VeyonObjectId == objectId))
                throw new InvalidDataException("这台 Veyon 电脑已经关联到其他座位。");
            var seats = layout.Seats.ToArray();
            seats[seatIndex] = seats[seatIndex] with
            {
                VeyonHostAddress = normalizedHost,
                VeyonComputerName = computerName,
                VeyonObjectId = objectId,
                VeyonLocationObjectId = locationObjectId
            };
            return layout with { Seats = seats };
        });
    }

    /// <summary>Assigns a fully verified room plan in one local layout write.</summary>
    public ClassroomSeatGridLayout AssignVeyonTargets(Guid campusProfileId, Guid roomId,
        IReadOnlyList<ClassroomSeatGridTargetBinding> bindings)
    {
        ValidateIdentity(campusProfileId, roomId);
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count is < 1 or > MaximumSeats)
            throw new InvalidDataException($"电脑关联数量必须为 1–{MaximumSeats} 台。");

        var normalizedBindings = bindings.Select(binding =>
        {
            ArgumentNullException.ThrowIfNull(binding);
            var host = VeyonHostAddress.NormalizeOverride(binding.HostAddress);
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidDataException("Veyon 电脑地址不能为空。");
            ValidateVeyonComputerName(binding.ComputerName);
            if (binding.SeatId == Guid.Empty || binding.ObjectId == Guid.Empty ||
                binding.LocationObjectId == Guid.Empty)
                throw new InvalidDataException("Veyon 电脑或座位标识无效。");
            return binding with { HostAddress = host };
        }).ToArray();

        if (normalizedBindings.Select(binding => binding.SeatId).Distinct().Count() != normalizedBindings.Length ||
            normalizedBindings.Select(binding => binding.HostAddress)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedBindings.Length ||
            normalizedBindings.Select(binding => binding.ComputerName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedBindings.Length ||
            normalizedBindings.Select(binding => binding.ObjectId).Distinct().Count() != normalizedBindings.Length ||
            normalizedBindings.Select(binding => binding.LocationObjectId).Distinct().Count() != 1)
            throw new InvalidDataException("电脑清单有重复目标，或不属于同一个 Veyon 地点。");

        var bindingsBySeat = normalizedBindings.ToDictionary(binding => binding.SeatId);
        return Update(campusProfileId, roomId, layout =>
        {
            var namedSeats = layout.Seats.Where(seat => !string.IsNullOrWhiteSpace(seat.StudentName)).ToArray();
            if (namedSeats.Length != normalizedBindings.Length ||
                namedSeats.Any(seat => !bindingsBySeat.ContainsKey(seat.SeatId)))
                throw new InvalidDataException("座位姓名在写入期间发生变化；本次没有保存座位关联。");

            var seats = layout.Seats.Select(seat =>
            {
                if (!bindingsBySeat.TryGetValue(seat.SeatId, out var binding))
                    return seat with
                    {
                        VeyonHostAddress = null,
                        VeyonComputerName = null,
                        VeyonObjectId = null,
                        VeyonLocationObjectId = null
                    };
                return seat with
                {
                    VeyonHostAddress = binding.HostAddress,
                    VeyonComputerName = binding.ComputerName,
                    VeyonObjectId = binding.ObjectId,
                    VeyonLocationObjectId = binding.LocationObjectId
                };
            }).ToArray();
            return layout with { Seats = seats };
        });
    }

    public ClassroomSeatGridLayout ClearVeyonTarget(Guid campusProfileId, Guid roomId, Guid seatId)
    {
        ValidateIdentity(campusProfileId, roomId);
        if (seatId == Guid.Empty) throw new InvalidDataException("座位编号无效。");
        return Update(campusProfileId, roomId, layout =>
        {
            var seatIndex = Array.FindIndex(layout.Seats, seat => seat.SeatId == seatId);
            if (seatIndex < 0) throw new InvalidDataException("找不到要解除关联的座位。");
            var seats = layout.Seats.ToArray();
            seats[seatIndex] = seats[seatIndex] with
            {
                VeyonHostAddress = null, VeyonComputerName = null, VeyonObjectId = null,
                VeyonLocationObjectId = null
            };
            return layout with { Seats = seats };
        });
    }

    /// <summary>Swaps student names only; physical seats and their Veyon host mappings stay put.</summary>
    public ClassroomSeatGridLayout SwapStudents(Guid campusProfileId, Guid roomId,
        Guid firstSeatId, Guid secondSeatId)
    {
        ValidateIdentity(campusProfileId, roomId);
        if (firstSeatId == Guid.Empty || secondSeatId == Guid.Empty)
            throw new InvalidDataException("座位编号无效。");
        return Update(campusProfileId, roomId, layout =>
        {
            var firstIndex = Array.FindIndex(layout.Seats, seat => seat.SeatId == firstSeatId);
            var secondIndex = Array.FindIndex(layout.Seats, seat => seat.SeatId == secondSeatId);
            if (firstIndex < 0 || secondIndex < 0)
                throw new InvalidDataException("找不到要交换的座位。");
            if (string.IsNullOrWhiteSpace(layout.Seats[firstIndex].StudentName) ||
                string.IsNullOrWhiteSpace(layout.Seats[secondIndex].StudentName))
                throw new InvalidDataException("只能交换已填写学生姓名的两个座位。");
            var seats = layout.Seats.ToArray();
            (seats[firstIndex], seats[secondIndex]) =
                (seats[firstIndex] with { StudentName = seats[secondIndex].StudentName },
                    seats[secondIndex] with { StudentName = seats[firstIndex].StudentName });
            return layout with { Seats = seats };
        });
    }

    /// <summary>Commits a verified Veyon rename and the matching local student swap atomically.</summary>
    public ClassroomSeatGridLayout CommitStudentSwap(Guid campusProfileId, Guid roomId,
        Guid firstSeatId, Guid secondSeatId, string expectedFirstStudentName, string expectedSecondStudentName,
        Guid firstComputerObjectId, Guid firstLocationObjectId, string firstOldComputerName,
        string firstHostAddress, string firstComputerName,
        Guid secondComputerObjectId, Guid secondLocationObjectId, string secondOldComputerName,
        string secondHostAddress, string secondComputerName)
    {
        ValidateIdentity(campusProfileId, roomId);
        ValidateStudentName(expectedFirstStudentName);
        ValidateStudentName(expectedSecondStudentName);
        ValidateVeyonComputerName(firstOldComputerName);
        ValidateVeyonComputerName(secondOldComputerName);
        _ = VeyonHostAddress.NormalizeOverride(firstHostAddress);
        _ = VeyonHostAddress.NormalizeOverride(secondHostAddress);
        ValidateVeyonComputerName(firstComputerName);
        ValidateVeyonComputerName(secondComputerName);
        if (firstSeatId == Guid.Empty || secondSeatId == Guid.Empty || firstSeatId == secondSeatId ||
            firstComputerObjectId == Guid.Empty || firstLocationObjectId == Guid.Empty ||
            secondComputerObjectId == Guid.Empty || secondLocationObjectId == Guid.Empty ||
            firstComputerObjectId == secondComputerObjectId)
            throw new InvalidDataException("换座座位编号无效。");
        return Update(campusProfileId, roomId, layout =>
        {
            var firstIndex = Array.FindIndex(layout.Seats, seat => seat.SeatId == firstSeatId);
            var secondIndex = Array.FindIndex(layout.Seats, seat => seat.SeatId == secondSeatId);
            if (firstIndex < 0 || secondIndex < 0)
                throw new InvalidDataException("找不到要交换的座位。");
            var first = layout.Seats[firstIndex];
            var second = layout.Seats[secondIndex];
            if (string.IsNullOrWhiteSpace(first.StudentName) || string.IsNullOrWhiteSpace(second.StudentName) ||
                first.VeyonObjectId is null || second.VeyonObjectId is null ||
                first.VeyonObjectId == second.VeyonObjectId ||
                !string.Equals(first.StudentName, expectedFirstStudentName, StringComparison.Ordinal) ||
                !string.Equals(second.StudentName, expectedSecondStudentName, StringComparison.Ordinal) ||
                first.VeyonObjectId != firstComputerObjectId || second.VeyonObjectId != secondComputerObjectId ||
                first.VeyonLocationObjectId != firstLocationObjectId ||
                second.VeyonLocationObjectId != secondLocationObjectId ||
                !string.Equals(first.VeyonComputerName, firstOldComputerName, StringComparison.Ordinal) ||
                !string.Equals(second.VeyonComputerName, secondOldComputerName, StringComparison.Ordinal) ||
                !string.Equals(first.VeyonHostAddress, VeyonHostAddress.NormalizeOverride(firstHostAddress),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(second.VeyonHostAddress, VeyonHostAddress.NormalizeOverride(secondHostAddress),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("换座时两个座位都必须保持各自独立的 Veyon 电脑关联。");
            var seats = layout.Seats.ToArray();
            seats[firstIndex] = first with
            {
                StudentName = second.StudentName,
                VeyonComputerName = firstComputerName
            };
            seats[secondIndex] = second with
            {
                StudentName = first.StudentName,
                VeyonComputerName = secondComputerName
            };
            return layout with { Seats = seats };
        });
    }

    public static IReadOnlyList<ClassroomSeatGridCell> GetCells(ClassroomSeatGridLayout layout)
    {
        ValidateLayout(layout);
        var seatsByCoordinate = layout.Seats.ToDictionary(seat => (seat.Row, seat.Column));
        var cells = new ClassroomSeatGridCell[checked(layout.Rows * layout.Columns)];
        var index = 0;
        for (var row = 1; row <= layout.Rows; row++)
        for (var column = 1; column <= layout.Columns; column++)
        {
            if (seatsByCoordinate.TryGetValue((row, column), out var seat))
                cells[index++] = new ClassroomSeatGridCell(row, column, seat.ComputerNumber, seat.StudentName,
                    seat.VeyonHostAddress, seat.VeyonComputerName, seat.VeyonObjectId,
                    seat.VeyonLocationObjectId, seat.SeatId);
            else
                cells[index++] = new ClassroomSeatGridCell(row, column, null, null, null, null);
        }
        return Array.AsReadOnly(cells);
    }

    public static IReadOnlyList<ClassroomSeatGridLocation> GetLocations(ClassroomSeatGridLayout layout)
    {
        ValidateLayout(layout);
        return Array.AsReadOnly(layout.Seats.OrderBy(seat => seat.Row).ThenBy(seat => seat.Column)
            .Select(seat => new ClassroomSeatGridLocation(seat.SeatId, seat.ComputerNumber, seat.Row,
                seat.Column, seat.StudentName, seat.VeyonHostAddress, seat.VeyonComputerName,
                seat.VeyonObjectId, seat.VeyonLocationObjectId)).ToArray());
    }

    private ClassroomSeatGridLayout Update(Guid campusProfileId, Guid roomId,
        Func<ClassroomSeatGridLayout, ClassroomSeatGridLayout> update)
    {
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var document = File.Exists(_path)
                ? ReadDocument()
                : File.Exists(_legacyPath)
                    ? ReadLegacyDocument()
                    : new SeatGridDocument(CurrentSchemaVersion, []);
            var current = document.Layouts.SingleOrDefault(layout =>
                layout.CampusProfileId == campusProfileId && layout.RoomId == roomId)
                ?? CreateDefault(campusProfileId, roomId);
            var next = update(current);
            var updated = Replace(document, next);
            WriteDocument(updated);
            return Clone(next);
        }
    }

    private SeatGridDocument ReadDocument()
    {
        if (!File.Exists(_path)) return new SeatGridDocument(CurrentSchemaVersion, []);
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机座位图文件大小超出限制；原文件未修改。");

        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
                !versionElement.TryGetInt32(out var version))
                throw new InvalidDataException("本机座位图版本无效；原文件未修改。");
            if (version is not (OldestSupportedSchemaVersion or OlderSupportedSchemaVersion or
                PreviousSchemaVersion or CurrentSchemaVersion))
                throw new InvalidDataException("本机座位图版本不受支持；原文件未修改。");
            var document = JsonSerializer.Deserialize<SeatGridDocument>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("本机座位图为空；原文件未修改。");
            if (version < CurrentSchemaVersion)
            {
                document = document with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Layouts = document.Layouts?.Select(layout => layout is null
                        ? layout
                        : MigrateLegacyDefaultGrid(layout with { ComputerNumbersInitialized = false })).ToList()!
                };
            }
            ValidateDocument(document);
            if (version < CurrentSchemaVersion) WriteDocument(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("本机座位图格式无效；原文件未修改。", exception);
        }
    }

    private SeatGridDocument ReadLegacyDocument()
    {
        PathLinkSecurity.RejectLinks(_legacyPath);
        var info = new FileInfo(_legacyPath);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("旧版本机座位图文件大小超出限制；原文件未修改。");
        try
        {
            var bytes = File.ReadAllBytes(_legacyPath);
            PolicyJson.RejectDuplicateFields(bytes);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
                !versionElement.TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException("旧版本机座位图版本无效；原文件未修改。");
            var legacy = JsonSerializer.Deserialize<LegacySeatLayout>(bytes, JsonOptions)
                         ?? throw new InvalidDataException("旧版本机座位图为空；原文件未修改。");
            return MigrateLegacy(legacy);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("旧版本机座位图格式无效；原文件未修改。", exception);
        }
    }

    private void WriteDocument(SeatGridDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机座位图超过 16 MiB 限制；原文件未修改。");
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("本机座位图路径无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        if (File.Exists(_path)) PathLinkSecurity.RejectLinks(_path);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private FileStream AcquireFileLock()
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("本机座位图路径无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        var lockPath = _path + ".lock";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        IOException? lastFailure = null;
        do
        {
            try
            {
                if (File.Exists(lockPath)) PathLinkSecurity.RejectLinks(lockPath);
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                Thread.Sleep(25);
            }
        } while (timer.Elapsed < LockTimeout);
        throw new IOException("另一 TeacherConsole 实例正在更新座位图，请稍后重试。", lastFailure);
    }

    private static SeatGridDocument MigrateLegacy(LegacySeatLayout legacy)
    {
        if (legacy.SchemaVersion != 1 || legacy.Layouts is null || legacy.Layouts.Count > MaximumLayouts)
            throw new InvalidDataException("旧版座位排序文件无效；原文件未修改。");
        var migrated = new List<ClassroomSeatGridLayout>(legacy.Layouts.Count);
        foreach (var oldLayout in legacy.Layouts)
        {
            if (oldLayout is null || oldLayout.TargetOrder is null || oldLayout.TargetOrder.Length is < 1 or > MaximumSeats ||
                oldLayout.Columns is < 1 or > MaximumColumns)
                throw new InvalidDataException("旧版座位排序内容无效；原文件未修改。");
            if (oldLayout.CampusProfileId == Guid.Empty || oldLayout.RoomId == Guid.Empty)
                throw new InvalidDataException("旧版座位排序机房 ID 无效；原文件未修改。");
            // The old seat-order format used 25 columns as its default. Map that default
            // to today's 15-column canvas; the row-first target order remains unchanged.
            var columns = oldLayout.Columns == LegacyDefaultColumns
                ? DefaultColumns
                : Math.Max(DefaultColumns, oldLayout.Columns);
            var rows = Math.Max(DefaultRows,
                (int)Math.Ceiling(oldLayout.TargetOrder.Length / (double)columns));
            var seats = oldLayout.TargetOrder.Select((target, index) => new ClassroomSeatGridSeat(Guid.NewGuid(),
                index / columns + 1, index % columns + 1, string.Empty, null, target)).ToArray();
            var layout = new ClassroomSeatGridLayout(oldLayout.CampusProfileId, oldLayout.RoomId,
                rows, columns, seats, ComputerNumbersInitialized: false);
            ValidateLayout(layout);
            migrated.Add(layout);
        }
        var document = new SeatGridDocument(CurrentSchemaVersion, migrated);
        ValidateDocument(document);
        return document;
    }

    private static SeatGridDocument Replace(SeatGridDocument document, ClassroomSeatGridLayout layout)
    {
        var layouts = document.Layouts.Where(item => item.CampusProfileId != layout.CampusProfileId ||
                                                      item.RoomId != layout.RoomId)
            .Append(layout).ToList();
        return new SeatGridDocument(CurrentSchemaVersion, layouts);
    }

    private static ClassroomSeatGridLayout CreateDefault(Guid campusProfileId, Guid roomId) =>
        new(campusProfileId, roomId, DefaultRows, DefaultColumns, []);

    private static ClassroomSeatGridLayout InitializeLegacyComputerNumbers(ClassroomSeatGridLayout layout,
        int startNumber)
    {
        var ordered = layout.Seats.OrderBy(seat => seat.Row).ThenBy(seat => seat.Column).ToArray();
        var usedNumbers = ordered.Where(seat => seat.ComputerNumber is not null)
            .Select(seat => seat.ComputerNumber!.Value).ToHashSet();
        var nextNumber = startNumber;
        var seats = ordered.Select(seat =>
        {
            if (seat.ComputerNumber is not null) return seat;
            while (nextNumber <= MachineNaming.MaximumNumber && usedNumbers.Contains(nextNumber)) nextNumber++;
            if (nextNumber > MachineNaming.MaximumNumber)
                throw new InvalidDataException("旧版机房编号超出 1–150；原座位图保留，请先核对机房起始编号。");
            var numbered = seat with { ComputerNumber = nextNumber };
            usedNumbers.Add(nextNumber++);
            return numbered;
        }).ToArray();
        var initialized = layout with { Seats = seats, ComputerNumbersInitialized = true };
        ValidateLayout(initialized);
        return initialized;
    }

    private static ClassroomSeatGridLayout MigrateLegacyDefaultGrid(ClassroomSeatGridLayout layout)
    {
        // Earlier releases used a 25×25 canvas by default. Shrink only when every saved seat remains
        // inside 15×15, so existing seat records and their row-first numbers are preserved.
        if (layout.Rows != LegacyDefaultRows || layout.Columns != LegacyDefaultColumns || layout.Seats is null ||
            layout.Seats.Any(seat => seat is null || seat.Row > DefaultRows || seat.Column > DefaultColumns))
            return layout;
        return layout with { Rows = DefaultRows, Columns = DefaultColumns };
    }

    private static ClassroomSeatGridLayout Clone(ClassroomSeatGridLayout layout) =>
        layout with { Seats = layout.Seats.Select(seat => seat with { }).ToArray() };

    private static void ValidateDocument(SeatGridDocument? document)
    {
        if (document is null || document.SchemaVersion != CurrentSchemaVersion || document.Layouts is null ||
            document.Layouts.Count > MaximumLayouts)
            throw new InvalidDataException("本机座位图版本或数量无效；原文件未修改。");
        var identities = new HashSet<(Guid Campus, Guid Room)>();
        var assignedObjectIds = new HashSet<Guid>();
        var assignedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layout in document.Layouts)
        {
            ValidateLayout(layout);
            if (!identities.Add((layout.CampusProfileId, layout.RoomId)))
                throw new InvalidDataException("本机座位图包含重复校区/机房 ID。");
            foreach (var seat in layout.Seats)
            {
                if (seat.VeyonObjectId is { } objectId && !assignedObjectIds.Add(objectId))
                    throw new InvalidDataException("本机座位图将同一台 Veyon 电脑关联到了多个座位。");
                if (seat.VeyonHostAddress is { } host && !assignedHosts.Add(host))
                    throw new InvalidDataException("本机座位图将同一主机地址关联到了多个座位。");
            }
        }
    }

    private static void ValidateLayout(ClassroomSeatGridLayout? layout)
    {
        if (layout is null || layout.CampusProfileId == Guid.Empty || layout.RoomId == Guid.Empty ||
            layout.Seats is null || layout.Seats.Length > MaximumSeats)
            throw new InvalidDataException("座位图机房 ID 或座位数量无效。");
        ValidateDimensions(layout.Rows, layout.Columns);
        var seatIds = new HashSet<Guid>();
        var coordinates = new HashSet<(int Row, int Column)>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objectIds = new HashSet<Guid>();
        var computerNumbers = new HashSet<int>();
        foreach (var seat in layout.Seats)
        {
            if (seat is null || seat.SeatId == Guid.Empty || !seatIds.Add(seat.SeatId) ||
                seat.Row < 1 || seat.Row > layout.Rows || seat.Column < 1 || seat.Column > layout.Columns ||
                !coordinates.Add((seat.Row, seat.Column)))
                throw new InvalidDataException("座位图包含无效或重复的座位编号/位置。");
            if (seat.ComputerNumber is { } computerNumber &&
                (computerNumber is < 1 or > MachineNaming.MaximumNumber || !computerNumbers.Add(computerNumber)))
                throw new InvalidDataException("座位图电脑编号无效或重复；编号必须在 1–150 之间。");
            ValidateStudentName(seat.StudentName);
            if (seat.VeyonHostAddress is not null)
            {
                if (seat.VeyonComputerName is null)
                    throw new InvalidDataException("已关联的 Veyon 电脑必须同时包含地址和电脑名称。");
                var host = VeyonHostAddress.NormalizeOverride(seat.VeyonHostAddress);
                if (!string.Equals(host, seat.VeyonHostAddress, StringComparison.Ordinal) || !hosts.Add(host))
                    throw new InvalidDataException("座位图的 Veyon 地址无效或重复。");
            }
            if (seat.VeyonComputerName is not null)
            {
                ValidateVeyonComputerName(seat.VeyonComputerName);
                if (!names.Add(seat.VeyonComputerName))
                    throw new InvalidDataException("座位图的 Veyon 电脑名称重复。");
            }
            if (seat.VeyonObjectId is { } objectId && objectId == Guid.Empty)
                throw new InvalidDataException("座位图的 Veyon 电脑标识无效。");
            if (seat.VeyonObjectId is { } stableId && !objectIds.Add(stableId))
                throw new InvalidDataException("座位图的 Veyon 电脑标识重复。");
            if (seat.VeyonObjectId is not null &&
                (seat.VeyonHostAddress is null || seat.VeyonComputerName is null))
                throw new InvalidDataException("Veyon 稳定标识必须同时绑定电脑地址和名称。");
            if ((seat.VeyonLocationObjectId is { } locationId && locationId == Guid.Empty) ||
                (seat.VeyonObjectId is null) != (seat.VeyonLocationObjectId is null))
                throw new InvalidDataException("Veyon 地点稳定标识无效或不完整。");
        }
    }

    private static void ValidateDimensions(int rows, int columns)
    {
        if (rows is < 1 or > MaximumRows || columns is < 1 or > MaximumColumns ||
            (long)rows * columns > MaximumCells)
            throw new InvalidDataException($"座位图行数和列数必须在 1–{MaximumRows} 之间，且总格数不能超过 {MaximumCells}。");
    }

    private static void ValidateCoordinate(ClassroomSeatGridLayout layout, int row, int column)
    {
        if (row < 1 || row > layout.Rows || column < 1 || column > layout.Columns)
            throw new InvalidDataException("所选格子超出当前座位图范围。");
    }

    private static void ValidateStudentName(string? value)
    {
        if (value is null || value.Length > 100 || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException("学生姓名最多 100 个字符，不含首尾空格或控制字符。");
    }

    private static void ValidateVeyonComputerName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim() ||
            value.Any(char.IsControl))
            throw new InvalidDataException("Veyon 电脑名称须为 1–128 个字符，不含首尾空格或控制字符。");
    }

    private static void ValidateIdentity(Guid campusProfileId, Guid roomId)
    {
        if (campusProfileId == Guid.Empty || roomId == Guid.Empty)
            throw new InvalidDataException("座位图必须绑定有效的本机校区和机房 ID。");
    }
}
