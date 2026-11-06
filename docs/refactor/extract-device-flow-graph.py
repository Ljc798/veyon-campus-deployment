#!/usr/bin/env python3
"""Extract a small, reproducible device/session call graph from Graphify's AST graph."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / "graphify-out" / "graph.json"
OUTPUT = Path(__file__).with_name("device-flow-graph.json")
SOURCES = {
    "src/VeyonCampus.Core/ClassroomSessions.cs",
    "src/VeyonCampus.Core/ClassroomQuickControl.cs",
    "src/VeyonCampus.Core/TeacherCampusDirectory.cs",
    "src/VeyonCampus.Core/VeyonDeviceDirectoryStore.cs",
    "src/VeyonCampus.App/TeacherClassroomQuickControlService.cs",
    "src/VeyonCampus.App/TeacherViewModel.Classroom.cs",
    "src/VeyonCampus.App/TeacherViewModel.cs",
}
SYMBOL_TERMS = (
    "TeacherRoomProfile", "TeacherCampusDirectoryStore", "VeyonDeviceDirectoryStore",
    "ClassroomSession.Start", "ClassroomSession.UpdateScope", "ClassroomSessionStore.StartSession",
    "ClassroomSessionStore.UpdateActiveSession", "ClassroomSessionStore.EndSession",
    "VeyonDeviceDirectoryStore.Reconcile", "TeacherClassroomQuickControlService.SetClassroomContext",
    "TeacherClassroomQuickControlService.ReadSnapshot", "TeacherClassroomQuickControlService.ReadSavedSnapshot",
    "TeacherClassroomQuickControlService.BuildSnapshot", "TeacherClassroomQuickControlService.QueueEndedSessionRestores",
    "TeacherClassroomQuickControlService.RecoverPreviousProcessState",
    "TeacherViewModel.GetActiveClassroomEventContext", "TeacherViewModel.ResolveClassroomNetworkTargets",
    "TeacherViewModel.UpdateActiveClassroomScopeAsync", "TeacherViewModel.ReadClassroomQuickControlSnapshot",
    "TeacherViewModel.ReadActiveSeatLabels", "TeacherViewModel.ClassroomRoomContextSummary",
    "TeacherViewModel.HasPendingClassroomScopeChange", "TeacherViewModel.CanToggleClassroomSession",
    "TeacherViewModel.RefreshClassroomSessionProperties",
)

graph = json.loads(INPUT.read_text(encoding="utf-8"))
nodes = [node for node in graph["nodes"] if node.get("source_file") in SOURCES and
         any(term in node.get("label", "") for term in SYMBOL_TERMS)]
node_ids = {node["id"] for node in nodes}
links = [link for link in graph["links"]
         if link.get("source") in node_ids and link.get("target") in node_ids]
payload = {
    "source": "graphify-out/graph.json",
    "scope": sorted(SOURCES),
    "limitations": [
        "AST graph only; it does not include AXAML bindings or SQL schema references.",
        "Graphify reported a parser error in tests/VeyonCampus.Checks/Program.cs.",
        "This is a source-symbol/call graph, not proof of runtime state on a teacher PC.",
    ],
    "nodes": [{key: node[key] for key in ("id", "label", "source_file", "source_location", "_callable")
               if key in node} for node in nodes],
    "links": [{key: link[key] for key in ("source", "target", "relation", "source_file", "source_location")
               if key in link} for link in links],
}
OUTPUT.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"wrote {OUTPUT.relative_to(ROOT)}: {len(nodes)} nodes, {len(links)} links")
