# Competency questions: mapping to the model and to the instance files

Each competency question (Table 1 of the paper) asks for information that a simulation needs. The table below shows which classes and associations of the conceptual model (Fig. 1) each question exercises, where that information lives in an instance, and how `validate_instance.py` answers it.

`EFSM` = behavioural specification (`efsm_*.json`); `SPEC` = device specification (`device_spec_*.json`).

| ID | Competency question | Model elements | Instance fields | How it is answered |
|---|---|---|---|---|
| CQ1 | Which Components does a Device comprise and which are Materials or have a StateMachine? | Device, Component, Material, StateMachine | EFSM `components_catalog`, `scene_props`, `excluded_components`, `new_elements`; `category = material` | Union of component ids; those in `components_catalog` have a StateMachine |
| CQ2 | What is the Representation (shape, size, position, pivot) of a Component? | Component, Representation | SPEC `components[].representation`, `placement`, `placement.pivot` | Read per interactive component |
| CQ3 | Which StateVariables and States does a StateMachine define, and what are their initial values? | StateMachine, StateVariable, State | EFSM `components_catalog[].dimensions` (`values`, `initial`), `machines[].states`, `initial_state` | Read per machine |
| CQ4 | Which Transitions leave a given State and under which guard? | State, Transition (guard), State timeout | EFSM `machines[].transitions` (`from`, `guard`), `states[].timeout` | Transitions whose `from` matches the state's value of their dimension; timeouts listed as well |
| CQ5 | Which Interaction triggers a Transition? | Interaction, Transition (*triggers*) | SPEC `components[].movement`, `efsm_binding` (`press_action`, `action_at_max`, `action_at_min`) | Binding of each action to a component and interaction kind; actions fired by state timeouts are reported as such |
| CQ6 | Which Transitions are guarded by other Components and which signals do they emit? | Transition (guard, signal), StateVariable | EFSM `transitions[].guard`, `emits` | Transitions whose guard names another machine; emitted signals |
| CQ7 | Which Procedures does a Device comprise? | Device, Procedure | EFSM `procedures` | Listed |
| CQ8 | Which Steps form a Procedure, in which order, and which Transitions do they perform? | Procedure, Step (order, external), Transition (*performs*) | EFSM `steps[].order`, `transition_ref`, `accepted_transition_refs`, `loop` | Steps sorted by order with the actions of their transitions |
| CQ9 | Which StateConditions are the pre- and postconditions of a Procedure? | Procedure, StateCondition (pre, post) | EFSM `procedures[].preconditions`, `postconditions` | Listed |
| CQ10 | Which Components does a Procedure involve? | Procedure, Component (*/involves*, derived) | Steps' transitions, pre/postconditions, guards | Components acted on by steps plus components that appear only in conditions or guards |
| CQ11 | Which Rules govern a Procedure and which can be evaluated? | Rule (expression), Procedure, Transition | EFSM `rules[]` (`expr`, `applies_to`) | Count; evaluable when `expr` is not null |
| CQ12 | Which Procedures does a Procedure require? | Procedure (*requires*) | EFSM `procedures[].requires`, `preconditions[].resolver_procedure_id` | Listed; n/a when no procedure requires another |
| CQ13 | Which state variables, actions and signals does a Device use? | Device, StateVariable, Transition (action, signal) | EFSM `enums.dimensions`, `enums.actions`, `enums.signals` | Counts of the vocabulary |
| CQ14 | Which Materials does a Device process and which values can their variables take? | Material, StateVariable | EFSM `components_catalog[]` with `category = material`, `dimensions[].values` | Listed |

## Coverage of the model

| Model element | Questions |
|---|---|
| Device | CQ1, CQ7, CQ13 |
| Component | CQ1, CQ2, CQ10 |
| Material | CQ1, CQ14 |
| Representation | CQ2 |
| Interaction, *triggers* | CQ5 |
| StateMachine | CQ1, CQ3 |
| StateVariable | CQ3, CQ6, CQ13, CQ14 |
| State (incl. timeout) | CQ3, CQ4 |
| Transition (action, guard, signal) | CQ4, CQ5, CQ6, CQ8, CQ13 |
| Procedure | CQ7, CQ8, CQ9, CQ10, CQ11, CQ12 |
| Step, *performs* | CQ8 |
| StateCondition (pre, post) | CQ9 |
| Rule | CQ11 |
| *requires* | CQ12 |
| */involves* | CQ10 |

## n/a answers

A question is marked n/a when the instance contains no element of the kind it asks about, for example CQ12 in an instance with a single procedure. An n/a answer does not mean that the model lacks a concept. A question that the model cannot answer for an instance that does contain such elements would mean that.
