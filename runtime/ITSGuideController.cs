using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using TMPro;

namespace EFSM
{
    [System.Serializable]
    public class StepLogEntry
    {
        public int stepOrder;
        public string description;
        public float enteredAtSeconds;
        public float completedAtSeconds = -1f;
        public int failureCount;
    }

    [System.Serializable]
    public class SessionLog
    {
        public string deviceName;
        public string procedureId;
        public List<StepLogEntry> steps = new List<StepLogEntry>();
    }

    /// <summary>
    /// Capa de guia adaptativa (Fase 06). Se apoya por completo en eventos ya
    /// existentes de EfsmStateManager — no sustituye a ProcedureUIController,
    /// lo complementa: ProcedureUIController muestra el paso actual,
    /// ITSGuideController decide CUANDO ese texto deja de ser suficiente.
    ///
    /// Principios de diseno para el contexto BIF de la investigacion (sin
    /// implementar, por ausencia deliberada): sin temporizador, sin penalizacion
    /// por repetir un paso, sin presion de tiempo de ningun tipo. El umbral de
    /// fallos solo cambia el TEXTO y anade un resalte visual — nunca bloquea
    /// ni reduce intentos disponibles.
    ///
    /// CAMBIO (alineacion con ProcedureUIController): el componente a resaltar se
    /// resuelve por transition_ref -> maquina que la contiene -> binder, que es el
    /// camino que SI esta poblado en el EFSM. postcondition/precondition.machine_id
    /// se conservan solo como respaldo por si un EFSM los rellena. Antes, si el
    /// paso no traia postcondition, el machine_id salia null y el pulso nunca se
    /// disparaba (el ITS escalaba el texto pero no resaltaba nada).
    /// </summary>
    public class ITSGuideController : MonoBehaviour
    {
        public EfsmStateManager stateManager;
        public EfsmJsonLoader loader;

        [Header("Escalada")]
        [Tooltip("Numero de fallos consecutivos en el mismo paso antes de mostrar la variante simplificada.")]
        public int simplifiedThreshold = 2;

        [Header("UI (opcional — si no se asigna, solo se registra en consola y en el log)")]
        public TMP_Text guidanceText;

        [Header("Resalte")]
        public Color highlightColor = new Color(1f, 0.85f, 0.2f);

        private readonly Dictionary<int, int> _failuresByStep = new Dictionary<int, int>();
        private SessionLog _log = new SessionLog();
        private StepLogEntry _currentLogEntry;

        private void OnEnable()
        {
            if (stateManager == null) stateManager = FindObjectOfType<EfsmStateManager>();
            if (loader == null && stateManager != null) loader = stateManager.loader;
            if (stateManager == null) return;

            stateManager.OnStepAdvanced += HandleStepAdvanced;
            stateManager.OnUnexpectedAction += HandleUnexpectedAction;
            stateManager.OnProcedureComplete += HandleProcedureComplete;
        }

        private void OnDisable()
        {
            if (stateManager == null) return;
            stateManager.OnStepAdvanced -= HandleStepAdvanced;
            stateManager.OnUnexpectedAction -= HandleUnexpectedAction;
            stateManager.OnProcedureComplete -= HandleProcedureComplete;
        }

        private void HandleStepAdvanced(StepDef step)
        {
            // Cierra el registro del paso anterior y abre uno nuevo — esta es la
            // entrada que alimentara directamente las metricas de la Fase 07
            // (tiempo por paso, numero de fallos antes de completarlo).
            if (_currentLogEntry != null)
                _currentLogEntry.completedAtSeconds = Time.time;

            _currentLogEntry = new StepLogEntry
            {
                stepOrder = step.order,
                description = step.description,
                enteredAtSeconds = Time.time
            };
            _log.steps.Add(_currentLogEntry);

            if (!_failuresByStep.ContainsKey(step.order))
                _failuresByStep[step.order] = 0;

            if (guidanceText != null) guidanceText.text = step.description;
        }

        private void HandleUnexpectedAction(StepDef currentStep, string attemptedAction, string reason)
        {
            if (currentStep == null) return;

            _failuresByStep.TryGetValue(currentStep.order, out var count);
            count++;
            _failuresByStep[currentStep.order] = count;
            if (_currentLogEntry != null && _currentLogEntry.stepOrder == currentStep.order)
                _currentLogEntry.failureCount = count;

            Debug.Log($"[ITSGuideController] Fallo #{count} en paso {currentStep.order} (motivo: {reason}).");

            if (count < simplifiedThreshold)
                return; // sin penalizacion, sin urgencia — simplemente se deja reintentar

            EscalateToSimplifiedGuidance(currentStep);
        }

        private void EscalateToSimplifiedGuidance(StepDef step)
        {
            var text = !string.IsNullOrEmpty(step.simplified_description)
                ? step.simplified_description
                : step.description;

            if (guidanceText != null) guidanceText.text = text;
            Debug.Log($"[ITSGuideController] Escalando a guia simplificada en paso {step.order}: \"{text}\"");

            if (string.IsNullOrEmpty(step.simplified_description))
                Debug.LogWarning($"[ITSGuideController] Paso {step.order} sin 'simplified_description' en el EFSM: " +
                                 "la escalada reutiliza el texto normal. Anade el campo al JSON para diferenciar.");

            var targetMachineId = ResolveTargetMachineId(step);
            if (string.IsNullOrEmpty(targetMachineId))
            {
                Debug.LogWarning($"[ITSGuideController] Paso {step.order}: no se pudo resolver el componente a resaltar " +
                                 "(sin transition_ref/accepted_transition_refs ni postcondition/precondition). Sin pulso.");
                return;
            }

            var binder = stateManager.GetBinder(targetMachineId);
            var pulse = binder != null ? binder.GetComponent<EfsmHighlightPulse>() : null;
            if (pulse != null) pulse.Pulse(highlightColor);
            else Debug.LogWarning($"[ITSGuideController] '{targetMachineId}' sin EfsmHighlightPulse; no se resalta.");
        }

        /// <summary>
        /// Resuelve el machineId del componente del paso. Camino primario (poblado
        /// en el EFSM): transition_ref -> maquina que contiene esa transicion. Si el
        /// paso es de eleccion (accepted_transition_refs), usa la primera. Respaldo:
        /// postcondition/precondition.machine_id, por si un EFSM los rellena.
        /// </summary>
        private string ResolveTargetMachineId(StepDef step)
        {
            var ldr = loader ?? (stateManager != null ? stateManager.loader : null);

            string tref = !string.IsNullOrEmpty(step.transition_ref)
                ? step.transition_ref
                : (step.accepted_transition_refs != null && step.accepted_transition_refs.Count > 0
                    ? step.accepted_transition_refs[0]
                    : null);

            if (!string.IsNullOrEmpty(tref) && ldr != null && ldr.Data != null)
            {
                var machine = ldr.Data.machines
                    .FirstOrDefault(m => m.transitions != null && m.transitions.Any(t => t.id == tref));
                if (machine != null) return machine.id;
            }

            // Respaldo: campos de estado del paso, si existieran.
            if (step.postcondition != null && !string.IsNullOrEmpty(step.postcondition.machine_id))
                return step.postcondition.machine_id;
            if (step.precondition != null && !string.IsNullOrEmpty(step.precondition.machine_id))
                return step.precondition.machine_id;

            return null;
        }

        private void HandleProcedureComplete()
        {
            if (_currentLogEntry != null) _currentLogEntry.completedAtSeconds = Time.time;
            ExportLog();
        }

        /// <summary>
        /// Vuelca el registro de la sesion a persistentDataPath (ruta valida tanto
        /// en escritorio como en el APK standalone de Quest). Pensado como entrada
        /// directa de la Fase 07, no como sustituto de instrumentacion mas
        /// completa si la evaluacion con usuarios lo requiere.
        /// </summary>
        [ContextMenu("Export Session Log")]
        public void ExportLog()
        {
            _log.deviceName = loader != null && loader.Data != null ? loader.Data.device_meta.device_name : "unknown";
            _log.procedureId = stateManager != null ? stateManager.targetProcedureId : _log.procedureId;
            var json = JsonUtility.ToJson(_log, true);
            var path = Path.Combine(Application.persistentDataPath, $"its_log_{System.DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, json);
            Debug.Log($"[ITSGuideController] Log de sesion exportado a: {path}");
        }
    }
}