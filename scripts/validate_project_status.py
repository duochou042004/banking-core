#!/usr/bin/env python3
"""Validate the Banking Core progress snapshot without third-party packages."""

from __future__ import annotations

import argparse
import copy
import json
import re
import sys
from collections import defaultdict
from datetime import date
from pathlib import Path
from typing import Any
from urllib.parse import urlparse


ROOT_KEYS = {
    "$schema",
    "schema_version",
    "revision",
    "updated_on",
    "updated_by",
    "change_summary",
    "project",
    "state",
    "phases",
    "milestones",
    "next_gate",
    "blockers",
    "transitions",
}
PHASE_KEYS = {"id", "title", "status", "gate_status", "started_on", "completed_on", "evidence"}
MILESTONE_KEYS = {"id", "phase_id", "title", "status", "acceptance_criteria", "completed_on", "evidence"}
EVIDENCE_KEYS = {"kind", "ref", "description"}
BLOCKER_KEYS = {"id", "target_kind", "target_id", "summary", "owner", "opened_on", "resolution_condition"}
TRANSITION_KEYS = {"id", "target_kind", "target_id", "from", "to", "on", "summary", "evidence"}
LIFECYCLE = {"not_started", "in_progress", "blocked", "complete"}
GATE_BY_STATUS = {
    "not_started": "not_assessed",
    "in_progress": "in_progress",
    "blocked": "blocked",
    "complete": "met",
}
EVIDENCE_KINDS = {"artifact", "document", "revision", "test", "review"}
ID_PATTERN = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
PHASE_PATTERN = re.compile(r"^phase-[0-7]$")
ROADMAP_HEADING = re.compile(r"^## Phase ([0-7]) — (.+?)(?: \(complete (\d{4}-\d{2}-\d{2})\))?$")


class Validator:
    def __init__(self, root: Path, roadmap: dict[str, tuple[str, str | None]]) -> None:
        self.root = root
        self.roadmap = roadmap
        self.errors: list[str] = []

    def error(self, path: str, message: str) -> None:
        self.errors.append(f"{path}: {message}")

    def exact_object(self, value: Any, path: str, keys: set[str]) -> dict[str, Any]:
        if not isinstance(value, dict):
            self.error(path, "must be an object")
            return {}
        missing = keys - set(value)
        unexpected = set(value) - keys
        if missing:
            self.error(path, f"missing keys: {', '.join(sorted(missing))}")
        if unexpected:
            self.error(path, f"unexpected keys: {', '.join(sorted(unexpected))}")
        return value

    def array(self, value: Any, path: str) -> list[Any]:
        if not isinstance(value, list):
            self.error(path, "must be an array")
            return []
        return value

    def string(self, value: Any, path: str, *, identifier: bool = False, phase_id: bool = False) -> str | None:
        if not isinstance(value, str) or not value.strip():
            self.error(path, "must be a non-empty string")
            return None
        if identifier and not ID_PATTERN.fullmatch(value):
            self.error(path, "must be a lowercase hyphen-delimited identifier")
        if phase_id and not PHASE_PATTERN.fullmatch(value):
            self.error(path, "must match phase-[0-7]")
        return value

    def iso_date(self, value: Any, path: str, *, nullable: bool = False) -> date | None:
        if value is None and nullable:
            return None
        if not isinstance(value, str):
            self.error(path, "must be an ISO 8601 calendar date")
            return None
        try:
            parsed = date.fromisoformat(value)
        except ValueError:
            self.error(path, "must be an ISO 8601 calendar date")
            return None
        if parsed.isoformat() != value:
            self.error(path, "must use canonical YYYY-MM-DD form")
        return parsed

    def evidence(self, value: Any, path: str) -> list[dict[str, Any]]:
        entries = self.array(value, path)
        valid: list[dict[str, Any]] = []
        for index, raw in enumerate(entries):
            item_path = f"{path}[{index}]"
            item = self.exact_object(raw, item_path, EVIDENCE_KEYS)
            kind = self.string(item.get("kind"), f"{item_path}.kind")
            ref = self.string(item.get("ref"), f"{item_path}.ref")
            self.string(item.get("description"), f"{item_path}.description")
            if kind and kind not in EVIDENCE_KINDS:
                self.error(f"{item_path}.kind", f"must be one of {sorted(EVIDENCE_KINDS)}")
            if ref:
                self.evidence_ref(ref, f"{item_path}.ref")
            valid.append(item)
        return valid

    def evidence_ref(self, ref: str, path: str) -> None:
        parsed = urlparse(ref)
        if parsed.scheme:
            if parsed.scheme != "https" or not parsed.netloc:
                self.error(path, "web evidence must be an absolute HTTPS URL")
            return
        if not parsed.path:
            self.error(path, "local evidence must identify a repository path")
            return
        candidate = Path(parsed.path)
        if candidate.is_absolute():
            self.error(path, "local evidence must use a repository-relative path")
            return
        resolved = (self.root / candidate).resolve()
        try:
            resolved.relative_to(self.root)
        except ValueError:
            self.error(path, "local evidence escapes repository root")
            return
        if not resolved.exists():
            self.error(path, f"local evidence does not exist: {ref}")

    def no_percentages(self, value: Any, path: str = "$") -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                child_path = f"{path}.{key}"
                if "percent" in key.lower():
                    self.error(child_path, "percentage fields are prohibited; use evidence-backed lifecycle state")
                self.no_percentages(child, child_path)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                self.no_percentages(child, f"{path}[{index}]")

    def validate(self, raw: Any) -> list[str]:
        self.no_percentages(raw)
        document = self.exact_object(raw, "$", ROOT_KEYS)

        if document.get("$schema") != "./docs/delivery/project-status.schema.json":
            self.error("$.$schema", "must reference ./docs/delivery/project-status.schema.json")
        if document.get("schema_version") != 1 or isinstance(document.get("schema_version"), bool):
            self.error("$.schema_version", "must equal 1")
        revision = document.get("revision")
        if not isinstance(revision, int) or isinstance(revision, bool) or revision < 1:
            self.error("$.revision", "must be an integer of at least 1")
        updated_on = self.iso_date(document.get("updated_on"), "$.updated_on")
        self.string(document.get("updated_by"), "$.updated_by")
        self.string(document.get("change_summary"), "$.change_summary")

        project = self.exact_object(document.get("project"), "$.project", {"name", "repository", "roadmap"})
        if project.get("name") != "Banking Core":
            self.error("$.project.name", "must equal Banking Core")
        repository = self.string(project.get("repository"), "$.project.repository")
        if repository:
            parsed = urlparse(repository)
            if parsed.scheme != "https" or not parsed.netloc:
                self.error("$.project.repository", "must be an absolute HTTPS URL")
        if project.get("roadmap") != "docs/delivery/roadmap.md":
            self.error("$.project.roadmap", "must equal docs/delivery/roadmap.md")

        state = self.exact_object(
            document.get("state"),
            "$.state",
            {"active_phase", "last_completed_phase", "next_phase"},
        )
        for key in ("active_phase", "last_completed_phase", "next_phase"):
            value = state.get(key)
            if value is not None:
                self.string(value, f"$.state.{key}", phase_id=True)

        phases = self.validate_phases(document.get("phases"), updated_on)
        milestones = self.validate_milestones(document.get("milestones"), phases, updated_on)
        self.validate_derived_state(state, phases)
        self.validate_next_gate(document.get("next_gate"), phases, milestones)
        self.validate_blockers(document.get("blockers"), phases, milestones, updated_on)
        self.validate_transitions(document.get("transitions"), phases, milestones, updated_on)
        return self.errors

    def validate_phases(self, raw: Any, updated_on: date | None) -> dict[str, dict[str, Any]]:
        items = self.array(raw, "$.phases")
        if len(items) != 8:
            self.error("$.phases", "must contain exactly eight roadmap phases")
        phases: dict[str, dict[str, Any]] = {}
        statuses: list[str | None] = []

        for index, raw_phase in enumerate(items):
            path = f"$.phases[{index}]"
            phase = self.exact_object(raw_phase, path, PHASE_KEYS)
            phase_id = self.string(phase.get("id"), f"{path}.id", phase_id=True)
            title = self.string(phase.get("title"), f"{path}.title")
            status = phase.get("status")
            gate_status = phase.get("gate_status")
            if status not in LIFECYCLE:
                self.error(f"{path}.status", f"must be one of {sorted(LIFECYCLE)}")
                status = None
            if status and gate_status != GATE_BY_STATUS[status]:
                self.error(f"{path}.gate_status", f"must be {GATE_BY_STATUS[status]} when status is {status}")
            started_on = self.iso_date(phase.get("started_on"), f"{path}.started_on", nullable=True)
            completed_on = self.iso_date(phase.get("completed_on"), f"{path}.completed_on", nullable=True)
            evidence = self.evidence(phase.get("evidence"), f"{path}.evidence")

            expected_id = f"phase-{index}"
            if phase_id != expected_id:
                self.error(f"{path}.id", f"must be {expected_id} at this array position")
            if phase_id in phases:
                self.error(f"{path}.id", "duplicates another phase id")
            elif phase_id:
                phases[phase_id] = phase

            roadmap_entry = self.roadmap.get(expected_id)
            if roadmap_entry:
                roadmap_title, roadmap_completed = roadmap_entry
                if title != roadmap_title:
                    self.error(f"{path}.title", f"does not match roadmap title {roadmap_title!r}")
                status_completed = phase.get("completed_on") if status == "complete" else None
                if roadmap_completed != status_completed:
                    self.error(
                        f"{path}.completed_on",
                        f"must match roadmap completion marker {roadmap_completed!r}",
                    )

            if status == "not_started" and (started_on is not None or completed_on is not None):
                self.error(path, "not_started phases cannot have lifecycle dates")
            if status in {"in_progress", "blocked"}:
                if started_on is None:
                    self.error(f"{path}.started_on", f"is required when status is {status}")
                if completed_on is not None:
                    self.error(f"{path}.completed_on", f"must be null when status is {status}")
            if status == "complete":
                if completed_on is None:
                    self.error(f"{path}.completed_on", "is required for complete phases")
                if not evidence:
                    self.error(f"{path}.evidence", "complete phases require evidence")
            if started_on and completed_on and started_on > completed_on:
                self.error(path, "started_on cannot be after completed_on")
            for field_name, parsed in (("started_on", started_on), ("completed_on", completed_on)):
                if updated_on and parsed and parsed > updated_on:
                    self.error(f"{path}.{field_name}", "cannot be after updated_on")
            statuses.append(status)

        saw_incomplete = False
        for index, status in enumerate(statuses):
            if status != "complete":
                saw_incomplete = True
            elif saw_incomplete:
                self.error(f"$.phases[{index}].status", "completed phases must form a contiguous prefix")
        active = [index for index, status in enumerate(statuses) if status in {"in_progress", "blocked"}]
        if len(active) > 1:
            self.error("$.phases", "at most one phase may be active")
        first_incomplete = next((index for index, status in enumerate(statuses) if status != "complete"), None)
        if active and active[0] != first_incomplete:
            self.error(f"$.phases[{active[0]}].status", "only the first incomplete phase may be active")
        return phases

    def validate_milestones(
        self,
        raw: Any,
        phases: dict[str, dict[str, Any]],
        updated_on: date | None,
    ) -> dict[str, dict[str, Any]]:
        items = self.array(raw, "$.milestones")
        milestones: dict[str, dict[str, Any]] = {}
        by_phase: defaultdict[str, list[dict[str, Any]]] = defaultdict(list)
        for index, raw_milestone in enumerate(items):
            path = f"$.milestones[{index}]"
            milestone = self.exact_object(raw_milestone, path, MILESTONE_KEYS)
            milestone_id = self.string(milestone.get("id"), f"{path}.id", identifier=True)
            phase_id = self.string(milestone.get("phase_id"), f"{path}.phase_id", phase_id=True)
            self.string(milestone.get("title"), f"{path}.title")
            status = milestone.get("status")
            if status not in LIFECYCLE:
                self.error(f"{path}.status", f"must be one of {sorted(LIFECYCLE)}")
                status = None
            criteria = self.array(milestone.get("acceptance_criteria"), f"{path}.acceptance_criteria")
            if not criteria:
                self.error(f"{path}.acceptance_criteria", "must contain at least one criterion")
            for criterion_index, criterion in enumerate(criteria):
                self.string(criterion, f"{path}.acceptance_criteria[{criterion_index}]")
            completed_on = self.iso_date(milestone.get("completed_on"), f"{path}.completed_on", nullable=True)
            evidence = self.evidence(milestone.get("evidence"), f"{path}.evidence")
            if milestone_id in milestones:
                self.error(f"{path}.id", "duplicates another milestone id")
            elif milestone_id:
                milestones[milestone_id] = milestone
            if phase_id not in phases:
                self.error(f"{path}.phase_id", "references an unknown phase")
            elif phase_id:
                by_phase[phase_id].append(milestone)
            if status != "complete" and completed_on is not None:
                self.error(f"{path}.completed_on", "must be null unless status is complete")
            if status == "complete":
                if completed_on is None:
                    self.error(f"{path}.completed_on", "is required for complete milestones")
                if not evidence:
                    self.error(f"{path}.evidence", "complete milestones require evidence")
            if updated_on and completed_on and completed_on > updated_on:
                self.error(f"{path}.completed_on", "cannot be after updated_on")

        for phase_id, phase_milestones in by_phase.items():
            if phases[phase_id].get("status") == "complete":
                incomplete = [item.get("id") for item in phase_milestones if item.get("status") != "complete"]
                if incomplete:
                    self.error(f"$.phases[{phase_id}]", f"complete phase has incomplete milestones: {incomplete}")
        return milestones

    def validate_derived_state(self, state: dict[str, Any], phases: dict[str, dict[str, Any]]) -> None:
        ordered = [phases.get(f"phase-{index}") for index in range(8)]
        statuses = [phase.get("status") if phase else None for phase in ordered]
        active_index = next((index for index, status in enumerate(statuses) if status in {"in_progress", "blocked"}), None)
        completed_indices = [index for index, status in enumerate(statuses) if status == "complete"]
        next_index = next((index for index, status in enumerate(statuses) if status != "complete"), None)
        expected = {
            "active_phase": f"phase-{active_index}" if active_index is not None else None,
            "last_completed_phase": f"phase-{max(completed_indices)}" if completed_indices else None,
            "next_phase": f"phase-{next_index}" if next_index is not None else None,
        }
        for key, expected_value in expected.items():
            if state.get(key) != expected_value:
                self.error(f"$.state.{key}", f"must be derived as {expected_value!r}")

    def validate_next_gate(
        self,
        raw: Any,
        phases: dict[str, dict[str, Any]],
        milestones: dict[str, dict[str, Any]],
    ) -> None:
        incomplete_phases = [phase for phase in phases.values() if phase.get("status") != "complete"]
        if raw is None:
            if incomplete_phases:
                self.error("$.next_gate", "is required while a roadmap phase remains incomplete")
            return
        gate = self.exact_object(raw, "$.next_gate", {"kind", "id"})
        if gate.get("kind") != "milestone":
            self.error("$.next_gate.kind", "must equal milestone")
        gate_id = self.string(gate.get("id"), "$.next_gate.id", identifier=True)
        milestone = milestones.get(gate_id) if gate_id else None
        if milestone is None:
            self.error("$.next_gate.id", "next_gate references unknown milestone")
            return
        if milestone.get("status") == "complete":
            self.error("$.next_gate.id", "cannot reference a complete milestone")
        allowed_phases = {
            phase_id
            for phase_id, phase in phases.items()
            if phase.get("status") in {"in_progress", "blocked"}
        }
        first_incomplete = next(
            (f"phase-{index}" for index in range(8) if phases.get(f"phase-{index}", {}).get("status") != "complete"),
            None,
        )
        if first_incomplete:
            allowed_phases.add(first_incomplete)
        if milestone.get("phase_id") not in allowed_phases:
            self.error("$.next_gate.id", "must belong to the active or next roadmap phase")

    def validate_blockers(
        self,
        raw: Any,
        phases: dict[str, dict[str, Any]],
        milestones: dict[str, dict[str, Any]],
        updated_on: date | None,
    ) -> None:
        items = self.array(raw, "$.blockers")
        ids: set[str] = set()
        targeted: set[tuple[str, str]] = set()
        for index, raw_blocker in enumerate(items):
            path = f"$.blockers[{index}]"
            blocker = self.exact_object(raw_blocker, path, BLOCKER_KEYS)
            blocker_id = self.string(blocker.get("id"), f"{path}.id", identifier=True)
            if blocker_id in ids:
                self.error(f"{path}.id", "duplicates another blocker id")
            elif blocker_id:
                ids.add(blocker_id)
            kind = blocker.get("target_kind")
            if kind not in {"phase", "milestone"}:
                self.error(f"{path}.target_kind", "must be phase or milestone")
            target_id = self.string(blocker.get("target_id"), f"{path}.target_id", identifier=True)
            targets = phases if kind == "phase" else milestones if kind == "milestone" else {}
            target = targets.get(target_id) if target_id else None
            if target is None:
                self.error(f"{path}.target_id", "references an unknown target")
            elif target.get("status") != "blocked":
                self.error(f"{path}.target_id", "target status must be blocked")
            if kind and target_id:
                targeted.add((kind, target_id))
            for field in ("summary", "owner", "resolution_condition"):
                self.string(blocker.get(field), f"{path}.{field}")
            opened_on = self.iso_date(blocker.get("opened_on"), f"{path}.opened_on")
            if updated_on and opened_on and opened_on > updated_on:
                self.error(f"{path}.opened_on", "cannot be after updated_on")

        for kind, targets in (("phase", phases), ("milestone", milestones)):
            for target_id, target in targets.items():
                if target.get("status") == "blocked" and (kind, target_id) not in targeted:
                    self.error(f"$.{kind}s[{target_id}]", "blocked target requires an open blocker")

    def validate_transitions(
        self,
        raw: Any,
        phases: dict[str, dict[str, Any]],
        milestones: dict[str, dict[str, Any]],
        updated_on: date | None,
    ) -> None:
        items = self.array(raw, "$.transitions")
        ids: set[str] = set()
        previous_date: date | None = None
        by_target: defaultdict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
        for index, raw_transition in enumerate(items):
            path = f"$.transitions[{index}]"
            transition = self.exact_object(raw_transition, path, TRANSITION_KEYS)
            transition_id = self.string(transition.get("id"), f"{path}.id", identifier=True)
            if transition_id in ids:
                self.error(f"{path}.id", "duplicates another transition id")
            elif transition_id:
                ids.add(transition_id)
            kind = transition.get("target_kind")
            if kind not in {"phase", "milestone"}:
                self.error(f"{path}.target_kind", "must be phase or milestone")
            target_id = self.string(transition.get("target_id"), f"{path}.target_id", identifier=True)
            targets = phases if kind == "phase" else milestones if kind == "milestone" else {}
            if target_id not in targets:
                self.error(f"{path}.target_id", "references an unknown target")
            from_status = transition.get("from")
            to_status = transition.get("to")
            for field, status in (("from", from_status), ("to", to_status)):
                if status not in LIFECYCLE:
                    self.error(f"{path}.{field}", f"must be one of {sorted(LIFECYCLE)}")
            if from_status == to_status:
                self.error(path, "from and to statuses must differ")
            transition_date = self.iso_date(transition.get("on"), f"{path}.on")
            if previous_date and transition_date and transition_date < previous_date:
                self.error(f"{path}.on", "transitions must be append-only chronological order")
            if transition_date:
                previous_date = transition_date
            if updated_on and transition_date and transition_date > updated_on:
                self.error(f"{path}.on", "cannot be after updated_on")
            self.string(transition.get("summary"), f"{path}.summary")
            evidence = self.evidence(transition.get("evidence"), f"{path}.evidence")
            if to_status == "complete" and not evidence:
                self.error(f"{path}.evidence", "completion transitions require evidence")
            if kind in {"phase", "milestone"} and target_id:
                by_target[(kind, target_id)].append(transition)

        for (kind, target_id), transitions in by_target.items():
            target = (phases if kind == "phase" else milestones).get(target_id)
            for previous, current in zip(transitions, transitions[1:]):
                if current.get("from") != previous.get("to"):
                    self.error(
                        f"$.transitions[{current.get('id')}]",
                        "transition chain must start from the preceding target status",
                    )
            if target and transitions[-1].get("to") != target.get("status"):
                self.error(
                    f"$.transitions[{transitions[-1].get('id')}].to",
                    "latest transition must match current target status",
                )
            if target and target.get("status") == "complete":
                completion = next((item for item in reversed(transitions) if item.get("to") == "complete"), None)
                if completion is None:
                    self.error(f"$.{kind}s[{target_id}]", "complete target requires a completion transition")
                elif target.get("completed_on") != completion.get("on"):
                    self.error(f"$.{kind}s[{target_id}].completed_on", "must match its completion transition date")

        for kind, targets in (("phase", phases), ("milestone", milestones)):
            for target_id, target in targets.items():
                if target.get("status") != "not_started" and (kind, target_id) not in by_target:
                    self.error(f"$.{kind}s[{target_id}]", "active or complete target requires a lifecycle transition")


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot read valid JSON from {path}: {exc}") from exc


def load_roadmap(path: Path) -> dict[str, tuple[str, str | None]]:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise ValueError(f"cannot read roadmap {path}: {exc}") from exc
    result: dict[str, tuple[str, str | None]] = {}
    for line in lines:
        match = ROADMAP_HEADING.fullmatch(line)
        if match:
            number, title, completed_on = match.groups()
            result[f"phase-{number}"] = (title, completed_on)
    expected = {f"phase-{index}" for index in range(8)}
    if set(result) != expected:
        missing = sorted(expected - set(result))
        extra = sorted(set(result) - expected)
        raise ValueError(f"roadmap phase headings are invalid; missing={missing}, extra={extra}")
    return result


def validate_schema_contract(schema: Any) -> list[str]:
    errors: list[str] = []
    if not isinstance(schema, dict):
        return ["schema: root must be an object"]
    if schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        errors.append("schema.$schema: must use JSON Schema Draft 2020-12")
    if schema.get("type") != "object" or schema.get("additionalProperties") is not False:
        errors.append("schema: root must be a closed object")
    if set(schema.get("required", [])) != ROOT_KEYS:
        errors.append("schema.required: must exactly match project-status root fields")
    if set(schema.get("properties", {})) != ROOT_KEYS:
        errors.append("schema.properties: must exactly match project-status root fields")
    if not isinstance(schema.get("$defs"), dict):
        errors.append("schema.$defs: definitions object is required")
    return errors


def self_test(document: dict[str, Any], root: Path, roadmap: dict[str, tuple[str, str | None]]) -> list[str]:
    cases: list[tuple[str, Any, str]] = []

    percentage = copy.deepcopy(document)
    percentage["completion_percentage"] = 12
    cases.append(("percentage fields", percentage, "percentage fields are prohibited"))

    missing_evidence = copy.deepcopy(document)
    missing_evidence["phases"][0]["evidence"] = []
    cases.append(("completion evidence", missing_evidence, "complete phases require evidence"))

    skipped_phase = copy.deepcopy(document)
    skipped_phase["phases"][2].update(
        {"status": "complete", "gate_status": "met", "completed_on": document["updated_on"], "evidence": document["phases"][0]["evidence"]}
    )
    cases.append(("contiguous phases", skipped_phase, "completed phases must form a contiguous prefix"))

    unknown_gate = copy.deepcopy(document)
    unknown_gate["next_gate"]["id"] = "missing-milestone"
    cases.append(("unknown next gate", unknown_gate, "next_gate references unknown milestone"))

    title_drift = copy.deepcopy(document)
    title_drift["phases"][1]["title"] = "Almost the roadmap title"
    cases.append(("roadmap drift", title_drift, "does not match roadmap title"))

    escaped_evidence = copy.deepcopy(document)
    escaped_evidence["phases"][0]["evidence"][0]["ref"] = "../outside.md"
    cases.append(("evidence traversal", escaped_evidence, "escapes repository root"))

    # Activate a phase that genuinely has no recorded transition. Targeting a fixed index would
    # stop proving anything as soon as that phase legitimately acquired one.
    recorded = {
        transition.get("target_id")
        for transition in document.get("transitions", [])
        if transition.get("target_kind") == "phase"
    }
    unrecorded_index = next(
        (
            index
            for index in reversed(range(len(document["phases"])))
            if document["phases"][index].get("id") not in recorded
            and document["phases"][index].get("status") == "not_started"
        ),
        None,
    )
    if unrecorded_index is None:
        return ["self-test 'unrecorded lifecycle' has no phase without a transition to mutate"]

    unrecorded_start = copy.deepcopy(document)
    unrecorded_start["phases"][unrecorded_index].update(
        {"status": "in_progress", "gate_status": "in_progress", "started_on": document["updated_on"]}
    )
    cases.append(("unrecorded lifecycle", unrecorded_start, "requires a lifecycle transition"))

    failures: list[str] = []
    for name, mutated, expected in cases:
        errors = Validator(root, roadmap).validate(mutated)
        if not any(expected in error for error in errors):
            failures.append(f"self-test {name!r} did not detect {expected!r}")
    return failures


def main() -> int:
    default_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=default_root, help="repository root")
    parser.add_argument("--status", type=Path, help="status JSON; defaults to <root>/project-status.json")
    parser.add_argument("--schema", type=Path, help="schema JSON; defaults to the documented schema")
    parser.add_argument("--roadmap", type=Path, help="roadmap Markdown; defaults to the delivery roadmap")
    parser.add_argument("--self-test", action="store_true", help="also run mutation tests proving key checks fail closed")
    args = parser.parse_args()

    root = args.root.resolve()
    status_path = (args.status or root / "project-status.json").resolve()
    schema_path = (args.schema or root / "docs/delivery/project-status.schema.json").resolve()
    roadmap_path = (args.roadmap or root / "docs/delivery/roadmap.md").resolve()

    try:
        document = load_json(status_path)
        schema = load_json(schema_path)
        roadmap = load_roadmap(roadmap_path)
    except ValueError as exc:
        print(f"project status validation failed:\n- {exc}", file=sys.stderr)
        return 1

    errors = validate_schema_contract(schema)
    errors.extend(Validator(root, roadmap).validate(document))
    if args.self_test and not errors:
        errors.extend(self_test(document, root, roadmap))

    if errors:
        print("project status validation failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    suffix = " and validator self-tests passed" if args.self_test else " passed"
    print(f"project status revision {document['revision']}{suffix}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
