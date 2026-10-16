using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;

namespace EFSM
{
    public class EfsmStateManager : MonoBehaviour
    {
        public static EfsmStateManager Instance { get; private set; }

        public EfsmJsonLoader loader;
        [Tooltip("Procedimiento con el que arranca la simulación. Si se deja vacío, se usa el primero de device_meta.target_procedures.")]
        public string targetProcedureId;

        [Header("UI opcional — ProcedureUIController construye la suya propia por codigo")]
        public TMP_Text stepInstructionText;
        public TMP_Text componentStateText;
        public TMP_Text errorFeedbackText;

        [Header("Estados temporizados (states[].timeout)")]
        [Tooltip("Ejecuta los temporizadores declarados en el EFSM (p. ej. calentamiento de 25 s → listo).")]
        public bool runStateTimeouts = true;
        [Tooltip("Multiplicador del tiempo declarado. 1 = tiempo real del manual; valores menores solo para pruebas.")]
        public float timeoutScale = 1f;

        [Header("Cadena de procedimientos (device_meta.target_procedures)")]
        [Tooltip("Al terminar un procedimiento, arranca el siguiente de target_procedures. OnProcedureComplete se emite solo al final de la cadena.")]
        public bool chainTargetProcedures = true;

        public event Action<string, string, string> OnMachineStateChanged; // machineId, from, to
        public event Action<StepDef> OnStepAdvanced;
        public event Action OnProcedureComplete;
        public event Action<string> OnSignalEmitted;
        public event Action<string> OnGuardFailed;

        /// <summary>
        /// Evento añadido en la Fase 06 para el ITS: currentStep, accion intentada, motivo
        /// ("guard_not_satisfied" o "out_of_order"). Cubre dos casos que OnGuardFailed por si
        /// solo no distinguia: una guarda de transicion bloqueada, y una accion que SI tiene
        /// permiso a nivel de maquina pero no corresponde al paso activo del procedimiento.
        /// </summary>
        public event Action<StepDef, string, string> OnUnexpectedAction;

        /// <summary>
        /// Fase 06-bis (CAMINOS PARALELOS). Se emite cuando el punto actual del procedimiento
        /// ofrece MÁS DE UN paso posible (una bifurcación): el usuario puede realizar cualquiera
        /// de las acciones de la lista y el manager seguirá el camino elegido. La UI (por
        /// ejemplo ProcedureUIController) puede suscribirse para mostrar "haz A o B"; si no lo
        /// hace, CurrentStep sigue devolviendo el primer candidato y todo funciona igual, pero
        /// solo se resalta ese camino primario. OnStepAdvanced se sigue emitiendo con el
        /// candidato primario para no romper el contrato existente.
        /// </summary>
        public event Action<List<StepDef>> OnBranchOptions;

        /// <summary>Se emite al pasar de un procedimiento de la cadena al siguiente (id anterior, id siguiente).</summary>
        public event Action<string, string> OnProcedureChained;

        private readonly Dictionary<string, EfsmComponentBinder> _binders = new Dictionary<string, EfsmComponentBinder>();
        private readonly Dictionary<string, Dictionary<string, string>> _snapshot = new Dictionary<string, Dictionary<string, string>>();
        private ProcedureEntry _activeProcedure;

        // ── Ejecución con caminos paralelos ──────────────────────────────────────
        // Sustituye al antiguo _currentStepIndex (índice único). Ahora el "punto
        // actual" del procedimiento es un CONJUNTO de pasos candidatos: normalmente
        // uno solo (flujo lineal), y varios en un punto de bifurcación. La acción que
        // el usuario realiza selecciona cuál se confirma y define los siguientes.
        private readonly List<StepDef> _activeSteps = new List<StepDef>();
        private Dictionary<int, StepDef> _stepsByOrder = new Dictionary<int, StepDef>();
        private readonly Dictionary<int, int> _loopRemaining = new Dictionary<int, int>();

        // Temporizador en curso por máquina (se cancela si el estado cambia antes).
        private readonly Dictionary<string, Coroutine> _pendingTimeouts = new Dictionary<string, Coroutine>();

        public ProcedureEntry ActiveProcedure => _activeProcedure;

        private void Awake()
        {
            Instance = this;
            if (loader == null) loader = GetComponent<EfsmJsonLoader>();
        }

        private void OnEnable()
        {
            if (loader != null) loader.OnLoaded += HandleLoaded;
        }

        private void OnDisable()
        {
            if (loader != null) loader.OnLoaded -= HandleLoaded;
        }

        private void HandleLoaded()
        {
            _snapshot.Clear();
            foreach (var comp in loader.Data.components_catalog)
                _snapshot[comp.machine_id] = comp.dimensions.ToDictionary(d => d.name, d => d.initial);

            // Espera un frame antes de arrancar el procedimiento (ver nota original):
            // el orden de Start() entre GameObjects no está garantizado, así los
            // binders creados por el builder ya están registrados.
            StartCoroutine(StartAfterAllStarts());
        }

        private IEnumerator StartAfterAllStarts()
        {
            yield return null;

            foreach (var binder in FindObjectsOfType<EfsmComponentBinder>())
            {
                if (string.IsNullOrEmpty(binder.machineId)) continue;
                if (!_binders.ContainsKey(binder.machineId))
                    RegisterBinder(binder);
            }

            // Un estado inicial temporizado también debe correr su temporizador.
            foreach (var machineId in _snapshot.Keys.ToList())
                ScheduleStateTimeout(machineId);

            var startId = targetProcedureId;
            if (string.IsNullOrEmpty(startId))
            {
                var targets = loader.Data?.device_meta?.target_procedures;
                if (targets != null && targets.Count > 0) startId = targets[0];
            }

            if (!string.IsNullOrEmpty(startId))
                StartProcedure(startId);
        }

        /// <summary>Registra un binder ya inicializado para que sus cambios se reflejen en el snapshot global.</summary>
        public void RegisterBinder(EfsmComponentBinder binder)
        {
            if (string.IsNullOrEmpty(binder.machineId))
            {
                Debug.LogWarning("[EfsmStateManager] RegisterBinder ignorado: binder sin machineId. " +
                                 "Asegurate de que loader.Load() se ha llamado antes de Build().");
                return;
            }
            if (_binders.ContainsKey(binder.machineId)) return; // idempotente
            _binders[binder.machineId] = binder;
            binder.OnDimensionChanged += (dim, prev, next) =>
            {
                if (!_snapshot.ContainsKey(binder.machineId))
                    _snapshot[binder.machineId] = new Dictionary<string, string>();
                _snapshot[binder.machineId][dim] = next;
                OnMachineStateChanged?.Invoke(binder.machineId, prev, next);
                UpdateComponentStateText();
                ScheduleStateTimeout(binder.machineId);
            };
            Debug.Log($"[EfsmStateManager] {_binders.Count} binders registrados.");
        }

        public void StartProcedure(string procedureId)
        {
            _activeProcedure = loader.ProcedureById(procedureId);
            if (_activeProcedure == null)
            {
                Debug.LogError($"[EfsmStateManager] Procedimiento '{procedureId}' no encontrado.");
                return;
            }

            if (!loader.CheckPreconditions(_activeProcedure, _snapshot))
            {
                Debug.LogWarning($"[EfsmStateManager] Precondicion de entrada no satisfecha: {_activeProcedure.guard}");
                OnGuardFailed?.Invoke(_activeProcedure.guard);
                if (errorFeedbackText != null) errorFeedbackText.text = "Este procedimiento no puede iniciarse todavia.";
                return;
            }

            // Índice de pasos por 'order' (el flujo puede no ser lineal: las ramas
            // saltan a orders no contiguos vía branch.to_step).
            _stepsByOrder = (_activeProcedure.steps ?? new List<StepDef>())
                .Where(s => s != null)
                .GroupBy(s => s.order)
                .ToDictionary(g => g.Key, g => g.First());
            _loopRemaining.Clear();

            _activeSteps.Clear();
            var first = (_activeProcedure.steps ?? new List<StepDef>())
                .OrderBy(s => s.order).FirstOrDefault();
            if (first != null) _activeSteps.Add(first);

            Debug.Log($"[EfsmStateManager] ▶ Procedimiento iniciado: '{_activeProcedure.name}'");
            AnnounceActiveSteps();
        }

        private StepDef StepByOrder(int order) =>
            _stepsByOrder != null && _stepsByOrder.TryGetValue(order, out var s) ? s : null;

        /// <summary>
        /// Anuncia el/los paso(s) activo(s): actualiza el texto de instrucción con el
        /// candidato primario, emite OnStepAdvanced (contrato existente) y, si hay más
        /// de un candidato, OnBranchOptions. Autocompleta un paso externo solo cuando es
        /// el único candidato (con varias ramas la decisión es del usuario).
        /// </summary>
        private void AnnounceActiveSteps()
        {
            if (_activeSteps.Count == 0)
            {
                // Cadena: si target_procedures tiene un procedimiento siguiente, se arranca
                // y OnProcedureComplete se reserva para el final de la cadena.
                if (chainTargetProcedures && TryGetNextChained(out var nextId))
                {
                    var prevId = _activeProcedure != null ? _activeProcedure.id : null;
                    Debug.Log($"[EfsmStateManager] Procedimiento '{prevId}' completado → siguiente de la cadena: '{nextId}'.");
                    OnProcedureChained?.Invoke(prevId, nextId);
                    StartProcedure(nextId);
                    return;
                }

                Debug.Log("[EfsmStateManager] Procedimiento completado.");
                OnProcedureComplete?.Invoke();
                if (stepInstructionText != null) stepInstructionText.text = "Procedimiento completado.";
                return;
            }

            var primary = _activeSteps[0];

            if (stepInstructionText != null)
            {
                if (_activeSteps.Count > 1)
                {
                    var opts = string.Join("  ·  o bien  ·  ",
                        _activeSteps.Select(s => !string.IsNullOrEmpty(s.description) ? s.description : $"Paso {s.order}"));
                    stepInstructionText.text = "Elige un camino:  " + opts;
                }
                else
                {
                    stepInstructionText.text = !string.IsNullOrEmpty(primary.description) ? primary.description : $"Paso {primary.order}";
                }
            }

            OnStepAdvanced?.Invoke(primary);
            if (_activeSteps.Count > 1)
                OnBranchOptions?.Invoke(new List<StepDef>(_activeSteps));

            // Paso externo (acción del ordenador simulada) solo se autocompleta si es único.
            if (_activeSteps.Count == 1 && primary.external_step)
            {
                Debug.Log($"[EfsmStateManager] Paso {primary.order} es externo: {primary.description}");
                StartCoroutine(AutoCompleteExternalStep(primary));
            }
        }

        private bool TryGetNextChained(out string nextId)
        {
            nextId = null;
            if (_activeProcedure == null) return false;
            var targets = loader.Data?.device_meta?.target_procedures;
            if (targets == null) return false;
            int idx = targets.IndexOf(_activeProcedure.id);
            if (idx < 0 || idx + 1 >= targets.Count) return false;
            nextId = targets[idx + 1];
            return !string.IsNullOrEmpty(nextId);
        }

        [Header("Pasos externos")]
        [Tooltip("Segundos antes de auto-completar un paso externo (accion del ordenador, simulada).")]
        public float externalStepDelaySeconds = 2.5f;

        private IEnumerator AutoCompleteExternalStep(StepDef step)
        {
            yield return new WaitForSeconds(externalStepDelaySeconds);
            // Solo sigue vigente si ese paso externo continúa siendo el único candidato.
            if (_activeProcedure == null || _activeSteps.Count != 1 || _activeSteps[0] != step) yield break;
            Debug.Log($"[EfsmStateManager] Paso externo {step.order} auto-completado (simulado).");
            CommitStep(step);
        }

        // ── Estados temporizados ────────────────────────────────────────────────
        // Si el estado actual de una máquina declara timeout.after_ms, se programa el
        // paso a timeout.to_state. Si el estado cambia antes, el temporizador se cancela.

        private StateDef CurrentStateOf(MachineEntry machine)
        {
            if (machine?.states == null) return null;
            if (!_snapshot.TryGetValue(machine.id, out var cur)) return null;
            return machine.states.FirstOrDefault(s =>
                s?.dimension_values != null && s.dimension_values.Count > 0 &&
                s.dimension_values.All(kv => cur.TryGetValue(kv.Key, out var v) && v == kv.Value));
        }

        private void ScheduleStateTimeout(string machineId)
        {
            if (!runStateTimeouts || loader == null) return;

            if (_pendingTimeouts.TryGetValue(machineId, out var running) && running != null)
            {
                StopCoroutine(running);
                _pendingTimeouts.Remove(machineId);
            }

            var machine = loader.MachineById(machineId);
            var state = CurrentStateOf(machine);
            var to = state?.timeout;
            if (to == null || !to.after_ms.HasValue || to.after_ms.Value <= 0 || string.IsNullOrEmpty(to.to_state)) return;

            float seconds = Mathf.Max(0f, to.after_ms.Value / 1000f * timeoutScale);
            _pendingTimeouts[machineId] = StartCoroutine(FireStateTimeout(machineId, state.id, seconds));
        }

        private IEnumerator FireStateTimeout(string machineId, string fromStateId, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            _pendingTimeouts.Remove(machineId);

            var machine = loader.MachineById(machineId);
            var state = CurrentStateOf(machine);
            if (state == null || state.id != fromStateId) yield break; // el estado ya cambió

            var target = machine.states.FirstOrDefault(s => s.id == state.timeout.to_state);
            if (target?.dimension_values == null)
            {
                Debug.LogWarning($"[EfsmStateManager] Timeout de '{fromStateId}' apunta a un estado inexistente '{state.timeout.to_state}'.");
                yield break;
            }

            // ¿Hay una transición declarada que realice este cambio (p. ej. heating_complete)?
            var cur = _snapshot[machineId];
            var t = machine.transitions?.FirstOrDefault(tr =>
                tr != null && target.dimension_values.TryGetValue(tr.dimension, out var toVal) &&
                cur.TryGetValue(tr.dimension, out var curVal) && curVal == tr.from && tr.to == toVal);

            Debug.Log($"[EfsmStateManager] Timeout: {machineId} '{fromStateId}' → '{target.id}' tras {seconds:0.#} s.");

            if (t != null && _activeSteps.Any(st => StepAccepts(st, t.id)))
            {
                // El paso activo espera esta transición: se confirma como cualquier otra acción.
                TriggerAction(machineId, t.action);
                yield break;
            }

            // Cambio de sistema que ningún paso espera: se aplica sin contarlo como acción del usuario.
            if (_binders.TryGetValue(machineId, out var binder))
            {
                foreach (var kv in target.dimension_values)
                    if (!cur.TryGetValue(kv.Key, out var v) || v != kv.Value)
                        binder.SetDimensionValue(kv.Key, kv.Value);
            }
            else
            {
                foreach (var kv in target.dimension_values) cur[kv.Key] = kv.Value;
                UpdateComponentStateText();
            }

            var sig = t != null && !string.IsNullOrEmpty(t.emits) ? t.emits : state.timeout.emit_signal;
            if (!string.IsNullOrEmpty(sig)) OnSignalEmitted?.Invoke(sig);
        }

        /// <summary>[Debug] Avanza por el camino PRIMARIO sin realizar la acción física.</summary>
        [ContextMenu("Force Next Step")]
        public void ForceNextStep()
        {
            if (_activeProcedure == null || _activeSteps.Count == 0) return;
            CommitStep(_activeSteps[0]);
        }

        /// <summary>
        /// Confirma un paso como realizado y calcula el/los siguiente(s) candidato(s).
        /// </summary>
        private void CommitStep(StepDef committed)
        {
            var next = NextCandidates(committed);
            _activeSteps.Clear();
            _activeSteps.AddRange(next);
            AnnounceActiveSteps();
        }

        /// <summary>
        /// Calcula los pasos candidatos que siguen a `s`:
        ///  - loop (por número de repeticiones o until_guard) → vuelve al primer paso del bucle;
        ///  - branches (caminos paralelos) → los pasos destino (to_step) cuyas guardas se cumplan;
        ///    una rama con goto_procedure y guarda satisfecha deriva a otro procedimiento;
        ///  - sin lo anterior → el paso lineal siguiente (order+1), o fin del procedimiento.
        /// </summary>
        private List<StepDef> NextCandidates(StepDef s)
        {
            // 1) Bucle acotado.
            if (s.loop != null && s.loop.repeat_step_orders != null && s.loop.repeat_step_orders.Count > 0)
            {
                bool repeat;
                if (s.loop.times > 0)
                {
                    if (!_loopRemaining.TryGetValue(s.order, out var rem)) rem = s.loop.times;
                    repeat = rem > 0;
                    if (repeat) _loopRemaining[s.order] = rem - 1;
                }
                else if (!string.IsNullOrEmpty(s.loop.until_guard))
                {
                    // Repite mientras la guarda de salida NO se cumpla.
                    repeat = !GuardEvaluator.Evaluate(s.loop.until_guard, _snapshot);
                }
                else repeat = false;

                if (repeat)
                {
                    var back = StepByOrder(s.loop.repeat_step_orders[0]);
                    if (back != null) return new List<StepDef> { back };
                }
            }

            // 2) Ramas / caminos paralelos.
            if (s.branches != null && s.branches.Count > 0)
            {
                var cands = new List<StepDef>();
                foreach (var b in s.branches)
                {
                    if (b == null) continue;
                    if (!string.IsNullOrEmpty(b.guard) && !GuardEvaluator.Evaluate(b.guard, _snapshot))
                        continue; // esta rama no está disponible ahora

                    if (b.to_step > 0)
                    {
                        var target = StepByOrder(b.to_step);
                        if (target != null && !cands.Contains(target)) cands.Add(target);
                    }
                    else if (!string.IsNullOrEmpty(b.goto_procedure))
                    {
                        // Derivación a otro procedimiento: se toma de inmediato si su guarda
                        // se cumple (o no tiene). StartProcedure reinicia _activeSteps.
                        Debug.Log($"[EfsmStateManager] Rama goto_procedure → '{b.goto_procedure}'.");
                        if (!string.IsNullOrEmpty(b.emit_signal)) OnSignalEmitted?.Invoke(b.emit_signal);
                        StartProcedure(b.goto_procedure);
                        return new List<StepDef>(_activeSteps);
                    }
                }
                if (cands.Count > 0) return cands;
            }

            // 3) Camino lineal: siguiente por order, o fin.
            var lin = StepByOrder(s.order + 1);
            return lin != null ? new List<StepDef> { lin } : new List<StepDef>();
        }

        /// <summary>
        /// Punto de entrada unico para que los componentes interactivos (ConstrainedMover
        /// al soltar, EfsmPressTrigger al activarse, comportamientos personalizados)
        /// notifiquen una accion intentada. Valida la transicion y su guarda, aplica el
        /// cambio de dimension y, si la accion corresponde a alguno de los pasos activos
        /// (incluida una rama), confirma ese camino.
        /// </summary>
        public void TriggerAction(string machineId, string action)
        {
            if (string.IsNullOrEmpty(action)) return;

            var machine = loader.MachineById(machineId);
            if (machine == null)
            {
                var known = loader.Data != null
                    ? string.Join(", ", loader.Data.machines.Select(m => m.id))
                    : "(EFSM no cargado)";
                Debug.LogWarning($"[EfsmStateManager] Maquina '{machineId}' no encontrada en el EFSM " +
                                 $"(accion intentada: '{action}'). Maquinas conocidas: [{known}]. " +
                                 "Revisar machine_id en el device_spec / binder.");
                return;
            }

            var currentDimValues = _snapshot.TryGetValue(machineId, out var dims) ? dims : new Dictionary<string, string>();

            var transition = machine.transitions.FirstOrDefault(t =>
                t.action == action &&
                currentDimValues.TryGetValue(t.dimension, out var current) &&
                current == t.from);

            if (transition == null)
            {
                Debug.LogWarning($"[EfsmStateManager] Sin transicion valida para accion '{action}' en '{machineId}' desde el estado actual.");
                return;
            }

            if (!string.IsNullOrEmpty(transition.guard) && !GuardEvaluator.Evaluate(transition.guard, _snapshot))
            {
                Debug.LogWarning($"[EfsmStateManager] Guarda no satisfecha para '{transition.id}': {transition.guard}");
                OnGuardFailed?.Invoke(transition.guard);
                OnUnexpectedAction?.Invoke(CurrentStep, action, "guard_not_satisfied");
                if (errorFeedbackText != null) errorFeedbackText.text = "Accion no disponible todavia.";
                return;
            }

            if (_binders.TryGetValue(machineId, out var binder))
                binder.SetDimensionValue(transition.dimension, transition.to);

            if (!string.IsNullOrEmpty(transition.emits))
                OnSignalEmitted?.Invoke(transition.emits);

            // ¿La transicion corresponde a alguno de los pasos activos (rama incluida)?
            var matched = _activeSteps.FirstOrDefault(st => StepAccepts(st, transition.id));
            if (matched != null)
            {
                Debug.Log($"[EfsmStateManager] Paso {matched.order} completado: {transition.action}" +
                          (_activeSteps.Count > 1 ? "  (camino elegido en bifurcacion)" : ""));
                CommitStep(matched);
            }
            else
            {
                // Transicion valida a nivel de maquina pero no es lo que el punto actual
                // del procedimiento esperaba (ningun candidato la acepta).
                Debug.LogWarning($"[EfsmStateManager] Accion '{action}' aplicada en '{machineId}' pero no corresponde a ningun paso activo.");
                OnUnexpectedAction?.Invoke(CurrentStep, action, "out_of_order");
            }
        }

        public string GetCurrentState(string machineId) =>
            _snapshot.TryGetValue(machineId, out var dims) ? string.Join(", ", dims.Select(d => $"{d.Key}={d.Value}")) : "-";

        /// <summary>
        /// Soporte runtime de accepted_transition_refs (esquema EFSM v2.1.0): un paso de
        /// eleccion declara varias transiciones equivalentes (p. ej. pulsar Espresso O
        /// Lungo con identica continuacion) y cualquiera de ellas completa el paso.
        /// transition_ref se mantiene como via unica para los pasos normales.
        /// </summary>
        private static bool StepAccepts(StepDef step, string transitionId)
        {
            if (step == null || string.IsNullOrEmpty(transitionId)) return false;
            if (!string.IsNullOrEmpty(step.transition_ref) && step.transition_ref == transitionId) return true;
            return step.accepted_transition_refs != null && step.accepted_transition_refs.Contains(transitionId);
        }

        public string GetDimensionValue(string machineId, string dimension) =>
            _snapshot.TryGetValue(machineId, out var dims) && dims.TryGetValue(dimension, out var v) ? v : null;

        public Dictionary<string, Dictionary<string, string>> GetSnapshot() => _snapshot;

        /// <summary>
        /// Paso activo PRIMARIO del procedimiento en curso, o null si no hay ninguno.
        /// En una bifurcacion es el primer candidato; usa OnBranchOptions / ActiveSteps
        /// para conocer todos los caminos posibles.
        /// </summary>
        public StepDef CurrentStep => _activeSteps.Count > 0 ? _activeSteps[0] : null;

        /// <summary>Todos los pasos candidatos ahora mismo (más de uno = bifurcación).</summary>
        public IReadOnlyList<StepDef> ActiveSteps => _activeSteps;

        /// <summary>Permite al ITS localizar el GameObject de un componente por su machineId.</summary>
        public EfsmComponentBinder GetBinder(string machineId) =>
            _binders.TryGetValue(machineId, out var b) ? b : null;

        private void UpdateComponentStateText()
        {
            if (componentStateText == null) return;
            componentStateText.text = string.Join("\n", _snapshot.Select(kv =>
                $"{kv.Key}: {string.Join(", ", kv.Value.Select(d => $"{d.Key}={d.Value}"))}"));
        }
    }
}
