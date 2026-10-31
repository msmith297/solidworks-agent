---
name: smart-rebuild
title: Smart Rebuild
description: Rebuild the whole design around a change far back in its history - a revised customer part, two cavities to three, a changed skeleton dimension - and end up with a design as if it had been built that way. Surveys how the file is built, agrees a staged plan with you, works on a copy with checkpoints, and keeps a journal so a second attempt learns from the first.
kind: tool
icon: rebuild
agent: true
intelligence: max
confirm: always
context: view, selection, model, history
created: 2026-10-15T00:00:00Z
updated: 2026-10-17T00:00:00Z
---

# Smart Rebuild (built-in SolidWorks Agent tool)

Change a design far back in its history and end up with a design as if it had been built that way: same intent, working features, on the new geometry. On a big multi-body part this is hard: done carelessly, one early edit cascades into dozens of broken features. Think like the engineer who built the file before touching it. Work in phases and don't skip any.

## The inputs (from the tool's page)
- `change`:
  - **New customer part**: replace the inserted customer part with the file in `new_part` (it arrives as views + measurements). If `new_part` is empty, ask for it (ask_user) before anything else.
  - **Describe the change**: make the change described in `notes`, then rebuild. If `notes` is empty, ask what to change.
  - **I'll make the change**: record the design first (phase 1), then ask the user to make the change (roll back, edit, roll forward) and wait until they say it's done.
  - **Already made (not saved)**: the user changed it but hasn't saved since: take the baseline from the last saved version (snapshot_design from_saved_file).
  - **Continue the last rebuild**: read the journal first and learn from it; if the working file is damaged, restore the last good checkpoint (checkpoint list / restore); then carry on from the last completed stage of the plan - revise the plan where the journal's lessons say so.
- `notes`: for the other choices, what the agent should know (what changed in the new part, what must stay, what can move).
- `copy` / `copy_name`: when `copy` is true, call save_working_copy (name: `copy_name`, empty for the default) before any change. If it says the active document already is a working copy, carry on in it. Never edit the original file when `copy` is true.
- `decisions`:
  - **Ask on big ones**: decide routine repairs yourself; ask (ask_form, with the options drawn on the model) before anything that changes function, cost, other parts or customer-facing geometry.
  - **Ask about all**: ask before every judgement call.
  - **Decide everything**: make the engineering calls yourself and report each one.
- `references`: drawings, images, PDFs or models showing the intended result.

The part must be open (typically a multi-body part such as a mold). If the active document isn't a part, say so and stop.

## Phase 1 - Survey (read only, no changes yet)
- rebuild_journal read: earlier attempts on this design, their plans and lessons. Never repeat a recorded mistake. When continuing, pick up from the journal's last stage.
- snapshot_design "baseline" (from_saved_file if the user already made the change), then analyze_design: inserted parts, the skeleton, where each body comes from, the features that pick bodies and the blast radius of early features. Read describe_model_history and look at the model (capture_views).
- Understand the anatomy: which features are shared (plates, skeleton, base), which repeat per instance (a cavity's kit of inserts, indents, cuts, vents), which do body housekeeping (Delete/Keep, Combine, Move/Copy). Write down the design intent of each group ("plate = cavity span + 2 in each side", "core pocket = part + 0.125 lip").

## Phase 2 - Strategy and plan (before any change)
- Choose the approach that changes the least of what already works:
  - **Additive**: keep everything that exists and append new features at the END of the tree (after the last body-picking feature analyze_design reports) - e.g. add a third cavity by copying/patterning the cavity kit and extending shared plates by their driving dimension, not by inserting bodies early. Usually best for adding instances.
  - **Edit in place**: change a driving dimension or swap the inserted part, then repair - when the change is a size/shape change that doesn't change how many bodies exist before the body-picking features.
  - **Rebuild a section**: delete a group and rebuild it cleanly on the new geometry - when patching would touch more than it keeps.
- Never change the number or order of bodies before body-picking features unless you also plan how each of them gets its bodies back.
- Write the plan: the strategy and why, ordered stages (each with its expected result and how you will check it), the risks, and the decisions the user should make. Show it on the model (show_overlay) and get the user's go-ahead with ask_form, offering alternatives where there is a real choice. Append the agreed plan to the journal.

## Phase 3 - Execute in stages
- When `copy` is true, the working copy must exist before the first change (save_working_copy).
- checkpoint save before each stage. Do the stage, rebuild, compare_design, and check the stage's expected result.
- If the stage made things worse and two attempts to fix it don't converge: checkpoint restore to the last good state, append the lesson to the journal (what you did, what broke, why, what to do instead), revise the plan, and continue - don't undo in circles and don't start over.
- Never close or reopen the document from your own code (it cuts the conversation off from the file); checkpoint restore is the only way back.
- Make the user's decisions as `decisions` says (decide routine ones, ask on big ones with options drawn on the model).

## Phase 4 - Finish
- Clean up the bodies (multi-body parts) once every stage is done and the rebuild is clean: put every body that is no longer part of the result into a solid-body folder named "Garbage" - old bodies the rebuild superseded (the previous version of an inserted part, a replaced cavity or insert, copies you made along the way) and tool bodies left over from boolean operations (bodies kept after a Combine, Indent, Cut-with-body or Split that only served as the tool). Reuse the "Garbage" folder if the part already has one. Don't delete them and don't move a body the finished design still needs (plates, inserts, the bodies the user works with); if it's unclear whether a body is leftover, leave it out and list it in the report. Check the folder afterwards (it holds exactly the bodies you meant) and name them in the report.
- compare_design with the baseline: no errors or warnings, every baseline feature still exists or its replacement is explained, bodies changed only as the change implies; check the intents you wrote down in phase 1. Append the outcome to the journal.
- Report: the strategy, what changed, what was repaired, the decisions, anything to check. Never save over the original file.

## Acceptance checks (verify before reporting success)
1. The rebuild has no errors or warnings (compare_design against the baseline).
2. Every baseline feature still exists, or its replacement is named and explained.
3. Bodies changed only as the change implies; leftover bodies are in "Garbage", none the design still needs.
4. The design intents recorded in phase 1 still hold (measure them).
5. With `copy` on, the original file on disk is unchanged.
