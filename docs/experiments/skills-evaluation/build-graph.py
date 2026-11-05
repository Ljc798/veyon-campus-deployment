#!/usr/bin/env python3
"""Build an evidence-backed knowledge graph for the Veyon location/device sync path.

This is a graphify-style static graph: nodes are anchored to a symbol in a source
file and the script verifies that the symbol still exists before emitting the
edge. It is deliberately not an AST parser - the goal is a reviewable, cited map
of the sync chain, not a claim of complete semantic analysis.
"""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
OUT = HERE / "location-sync-graph.json"

CORE = "src/VeyonCampus.Core/"
APP = "src/VeyonCampus.App/"
VIEWS = APP + "Presentation/Views/"
CHECKS = "tests/VeyonCampus.Checks/"

NODES = [
    {"id": "ui.retry", "label": "重试 Veyon 同步（座位图）", "kind": "ui",
     "file": VIEWS + "TeacherRoomView.axaml", "symbol": "RequestRetrySeatMapSync"},
    {"id": "ui.sync", "label": "RunSeatMapVeyonSyncAsync", "kind": "ui",
     "file": APP + "TeacherWindow.axaml.cs", "symbol": "RunSeatMapVeyonSyncAsync"},
    {"id": "vm.addroom", "label": "AddRoomToVeyonAsync", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.cs", "symbol": "AddRoomToVeyonAsync"},
    {"id": "vm.bind", "label": "TryBindRoomComputersToSeatGridAsync", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.cs", "symbol": "TryBindRoomComputersToSeatGridAsync"},
    {"id": "vm.refresh", "label": "RefreshDirectoryViewsAfterVeyonWriteAsync", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.cs", "symbol": "RefreshDirectoryViewsAfterVeyonWriteAsync"},
    {"id": "vm.summary", "label": "RefreshVeyonDeviceDirectorySummaryAsync", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.cs", "symbol": "RefreshVeyonDeviceDirectorySummaryAsync"},
    {"id": "vm.dupguard", "label": "RejectDuplicateVeyonLocations", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.cs", "symbol": "RejectDuplicateVeyonLocations"},
    {"id": "vm.apply", "label": "ApplyClassroomVeyonLocation", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.cs", "symbol": "ApplyClassroomVeyonLocation"},
    {"id": "vm.reconcilehost", "label": "ReconcileSeatVeyonBindingsByHostAsync", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.Classroom.cs", "symbol": "ReconcileSeatVeyonBindingsByHostAsync"},
    {"id": "vm.delete", "label": "DeleteSeatComputerFromVeyonAsync", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.Classroom.cs", "symbol": "DeleteSeatComputerFromVeyonAsync"},
    {"id": "vm.bindingstatus", "label": "VeyonRoomBindingStatus", "kind": "viewmodel",
     "file": APP + "TeacherViewModel.Classroom.cs", "symbol": "VeyonRoomBindingStatus"},
    {"id": "core.readlocations", "label": "ReadLocations (networkobjects export, 无 UUID)", "kind": "read",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "public static IReadOnlyList<VeyonNetworkLocation> ReadLocations()"},
    {"id": "core.readobjects", "label": "ReadNetworkObjects (networkobjects dump, 有 UUID)", "kind": "read",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "public static IReadOnlyList<VeyonNetworkObject> ReadNetworkObjects()"},
    {"id": "core.snapshot", "label": "ReadDirectorySnapshot (config export + 指纹)", "kind": "read",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "ReadDirectorySnapshot"},
    {"id": "core.parseobjects", "label": "ParseLocationsFromObjects (按名称合并)", "kind": "read",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "ParseLocationsFromObjects"},
    {"id": "core.saferead", "label": "SafeReadNetworkObjects (dump 失败回退)", "kind": "read",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "SafeReadNetworkObjects"},
    {"id": "core.configparse", "label": "ParseConfigurationDirectory", "kind": "read",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "ParseConfigurationDirectory"},
    {"id": "core.preview", "label": "BuildImportPreview (纯冲突检查)", "kind": "pure",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "BuildImportPreview"},
    {"id": "core.addlocation", "label": "AddLocation (逐台 add computer)", "kind": "write",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "public static VeyonLocationImportResult AddLocation"},
    {"id": "core.remove", "label": "RemoveComputer (按 UUID 删)", "kind": "write",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "public static VeyonComputerRemovalResult RemoveComputer"},
    {"id": "model.location", "label": "VeyonNetworkLocation (Name/Targets，无 UUID)", "kind": "model",
     "file": CORE + "VeyonNetworkLocation.cs", "symbol": "public sealed record VeyonNetworkLocation"},
    {"id": "model.room", "label": "TeacherRoomProfile (本机 Room，无地点 UUID)", "kind": "model",
     "file": CORE + "TeacherCampusDirectory.cs", "symbol": "public sealed record TeacherRoomProfile"},
    {"id": "store.campus", "label": "campus-directory.json (schema 2)", "kind": "store",
     "file": CORE + "TeacherCampusDirectory.cs", "symbol": "CurrentSchemaVersion = 2"},
    {"id": "store.seatgrid", "label": "classroom-seat-grid.json (schema 5)", "kind": "store",
     "file": CORE + "ClassroomSeatGridStore.cs", "symbol": "CurrentSchemaVersion = 5"},
    {"id": "store.devdir", "label": "veyon-device-directory.json (schema 1)", "kind": "store",
     "file": CORE + "VeyonDeviceDirectoryStore.cs", "symbol": "CurrentSchemaVersion = 1"},
    {"id": "core.assign", "label": "ClassroomSeatGridStore.AssignVeyonTargets", "kind": "write",
     "file": CORE + "ClassroomSeatGridStore.cs", "symbol": "AssignVeyonTargets"},
    {"id": "core.reconcile", "label": "VeyonDeviceDirectoryStore.Reconcile (含锁与写盘)", "kind": "write",
     "file": CORE + "VeyonDeviceDirectoryStore.cs", "symbol": "public VeyonDeviceDirectorySnapshot Reconcile"},
    {"id": "core.locationbyid", "label": "BuildLocationsById (本实验新增，按 UUID 投影)", "kind": "pure",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "BuildLocationsById", "added_in_experiment": True},
    {"id": "core.selectlocation", "label": "SelectLocationObject (本实验新增，按 UUID 选择)", "kind": "pure",
     "file": CORE + "VeyonNetworkObjectDirectory.cs", "symbol": "SelectLocationObject", "added_in_experiment": True},
    {"id": "checks.fixture", "label": "VeyonCampus.Checks 自建断言 runner", "kind": "test",
     "file": CHECKS + "Program.cs", "symbol": "void Expect(bool condition)"},
    {"id": "external.cli", "label": "veyon-cli networkobjects / config", "kind": "external",
     "file": None, "symbol": None},
    {"id": "external.veyon", "label": "Veyon BuiltinDirectory / 注册表", "kind": "external",
     "file": None, "symbol": None},
]

EDGES = [
    ("ui.retry", "ui.sync", "calls", "Click 事件 → TeacherWindow.RetrySeatMapSync"),
    ("ui.sync", "vm.reconcilehost", "calls", "RunSeatMapVeyonSyncAsync 第一步按 Host 对账"),
    ("ui.sync", "vm.addroom", "calls", "仍有未绑定座位时走建档"),
    ("vm.reconcilehost", "core.snapshot", "reads", "config export 读当前目录"),
    ("vm.reconcilehost", "core.assign", "writes", "ClearMissingVeyonTargets + AssignVeyonTargets"),
    ("vm.addroom", "core.preview", "reads", "InspectRoomConflictsAsync → ReadImportPreview"),
    ("vm.addroom", "core.addlocation", "calls", "提权 Worker → VeyonNetworkObjectDirectory.AddLocation"),
    ("vm.addroom", "store.campus", "writes", "TrySaveCurrentRoomProfile"),
    ("vm.addroom", "vm.bind", "calls", "写回成功后绑定座位"),
    ("vm.addroom", "vm.refresh", "calls", "finally 刷新三份名单"),
    ("vm.bind", "core.readobjects", "reads", "写入后读回 UUID 核对"),
    ("vm.bind", "core.assign", "writes", "AssignVeyonTargets 写座位绑定"),
    ("vm.bind", "core.selectlocation", "uses", "本实验：优先按写回 UUID 选地点"),
    ("vm.refresh", "core.readlocations", "reads", "策略/课堂页名单来自 CSV"),
    ("vm.refresh", "vm.summary", "calls", "刷新 UUID 设备目录摘要"),
    ("vm.refresh", "vm.dupguard", "calls", "同名地点阻断（现状只作用于已合并的 CSV）"),
    ("vm.summary", "core.readobjects", "reads", "dump 取 UUID 快照"),
    ("vm.summary", "core.parseobjects", "reads", "与 CSV 指纹交叉比对"),
    ("vm.summary", "core.reconcile", "writes", "Reconcile 内部持锁并写盘"),
    ("vm.summary", "core.locationbyid", "uses", "本实验：按 UUID 检测同名不同身份"),
    ("vm.apply", "model.location", "uses", "按 room.DisplayName 匹配地点（名称键）"),
    ("vm.bindingstatus", "store.seatgrid", "reads", "从座位级 UUID 反推地点绑定"),
    ("vm.delete", "core.remove", "calls", "先删 Veyon 对象"),
    ("vm.delete", "store.seatgrid", "writes", "随后 RestoreLayout 立即落盘"),
    ("core.readlocations", "external.cli", "runs", "networkobjects export（三列，无 UUID）"),
    ("core.readobjects", "external.cli", "runs", "networkobjects dump（带 ObjectId/ParentObjectId）"),
    ("core.snapshot", "external.cli", "runs", "config export（完整配置 + 指纹）"),
    ("core.saferead", "core.snapshot", "fallback", "dump 失败 → PartialRead 只读回退"),
    ("core.snapshot", "core.configparse", "uses", "解析 JsonStoreArray"),
    ("core.addlocation", "external.cli", "runs", "逐台 add computer <父地点 UUID>"),
    ("core.remove", "external.cli", "runs", "networkobjects remove <UUID>"),
    ("core.addlocation", "core.readobjects", "reads", "写前/写后读回校验"),
    ("core.remove", "core.snapshot", "reads", "删前/删后读回校验"),
    ("core.preview", "model.location", "uses", "按地点名比对"),
    ("core.assign", "store.seatgrid", "writes", "座位 UUID / 地点 UUID / Host"),
    ("core.reconcile", "store.devdir", "writes", "设备目录快照"),
    ("core.readlocations", "model.location", "produces", "按名称合并后的地点列表"),
    ("model.room", "store.campus", "stored_in", "Room 无地点 UUID"),
    ("external.cli", "external.veyon", "mutates", "BuiltinDirectory 配置"),
    ("core.configparse", "external.veyon", "reads", "注册表快照回退（0f67214）"),
    ("checks.fixture", "core.locationbyid", "asserts", "R19/R20 回归"),
    ("checks.fixture", "core.selectlocation", "asserts", "R21 回归"),
]


def git_head() -> str:
    try:
        return subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True, stderr=subprocess.DEVNULL
        ).strip()
    except Exception:
        return "unknown"


def main() -> None:
    cache: dict[str, list[str]] = {}
    nodes = []
    for node in NODES:
        item = dict(node)
        if node["file"] is None:
            item.update(line=None, verified=True, external=True)
        else:
            lines = cache.get(node["file"])
            if lines is None:
                path = ROOT / node["file"]
                if not path.exists():
                    raise SystemExit(f"missing source file: {path}")
                lines = path.read_text(encoding="utf-8-sig").splitlines()
                cache[node["file"]] = lines
            match = next(
                (index for index, text in enumerate(lines, 1) if node["symbol"] in text),
                None,
            )
            item.update(line=match, verified=match is not None, external=False)
        nodes.append(item)

    edges = [
        {"source": source, "target": target, "relation": relation, "evidence": evidence}
        for source, target, relation, evidence in EDGES
    ]
    ids = {node["id"] for node in nodes}
    endpoints = {endpoint for edge in edges for endpoint in (edge["source"], edge["target"])}
    dangling = sorted(endpoints - ids)
    if dangling:
        raise SystemExit(f"dangling graph endpoints: {dangling}")

    graph = {
        "schema": "graphify-location-sync/1",
        "source_ref": git_head(),
        "scope": "Veyon 地点 / 电脑 / 设备目录同步链路",
        "method": "静态符号锚定 + 人工核对的调用/数据流边；非完整 AST 语义分析",
        "nodes": nodes,
        "edges": edges,
        "summary": {
            "nodes": len(nodes),
            "edges": len(edges),
            "unverified_nodes": [node["id"] for node in nodes if not node["verified"]],
            "read_sources": 3,
            "persistent_stores": 3,
        },
    }
    OUT.write_text(json.dumps(graph, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)}: {len(nodes)} nodes, {len(edges)} edges")
    print("unverified:", graph["summary"]["unverified_nodes"] or "none")


if __name__ == "__main__":
    main()
