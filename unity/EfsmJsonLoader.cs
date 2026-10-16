using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

namespace EFSM
{
    [Serializable] public class DeviceMeta
    {
        public string device_name;
        public string device_type;
        public List<string> target_procedures;
        public string source_manual;
    }

    [Serializable] public class DimensionDef
    {
        public string name;
        public List<string> values;
        public string initial;
    }

    [Serializable] public class PhysicalProperties
    {
        public float width_mm;
        public float depth_mm;
        public float height_mm;
    }

    [Serializable] public class ComponentEntry
    {
        public string id;
        public string name;
        public string category;
        public string machine_id;
        public List<DimensionDef> dimensions;
        public PhysicalProperties physical_properties;
    }

    [Serializable] public class TransitionDef
    {
        public string id;
        public string from;
        public string to;
        public string dimension;
        public string action;
        public string guard;
        public string emits;
    }

    /// <summary>
    /// Temporizador de un estado (esquema EFSM: states[].timeout). Si after_ms tiene
    /// valor, el runtime pasa la máquina a to_state cuando ha transcurrido ese tiempo
    /// sin que el estado cambie (p. ej. calentamiento de 25 s → listo).
    /// </summary>
    [Serializable] public class TimeoutDef
    {
        public int? after_ms;
        public string to_state;
        public string emit_signal;
    }

    /// <summary>
    /// Estado de una máquina. Antes no se cargaba (solo se usaban las transiciones);
    /// se carga ahora para poder ejecutar los temporizadores de estado.
    /// </summary>
    [Serializable] public class StateDef
    {
        public string id;
        public Dictionary<string, string> dimension_values;
        public TimeoutDef timeout;
    }

    [Serializable] public class MachineEntry
    {
        public string id;
        public List<StateDef> states;
        public List<TransitionDef> transitions;
    }

    [Serializable] public class StateRef
    {
        public string machine_id;
        public string dimension;
        public string required_value;
    }

    /// <summary>
    /// Rama alternativa de un paso (esquema EFSM: branches[]). Añadida en la Fase 06-bis
    /// para soportar PROCEDIMIENTOS CON CAMINOS PARALELOS (p. ej. tras cargar papel:
    /// ajustar guías O imprimir). Hasta ahora el loader descartaba este campo, así que
    /// las ramas eran solo documentación; ahora se cargan y EfsmStateManager las ejecuta.
    ///
    /// Semántica que consume el runtime:
    ///  - `to_step`: order del paso al que salta esta rama (0 = sin salto).
    ///  - `goto_procedure`: id de otro procedimiento al que derivar (opcional).
    ///  - `guard`: si está presente, la rama solo se ofrece cuando la guarda es cierta.
    ///  - `kind`: "choice" | "alternative" | "failure" | "diagnostic" (informativo para el ITS).
    /// </summary>
    [Serializable] public class BranchDef
    {
        public string id;
        public string kind;
        public string guard;
        public int to_step;
        public string goto_procedure;
        public string emit_signal;
        public string description;
    }

    /// <summary>
    /// Bucle acotado de un paso (esquema EFSM: loop). Cargado para no perder el dato;
    /// EfsmStateManager soporta la variante por número de repeticiones (`times`) sobre
    /// `repeat_step_orders`. `until_guard` se evalúa al cierre de cada vuelta si está.
    /// </summary>
    [Serializable] public class LoopDef
    {
        public List<int> repeat_step_orders = new List<int>();
        public int times;
        public string until_guard;
    }

    [Serializable] public class StepDef
    {
        public int order;
        public string transition_ref;
        public bool external_step;
        public string description;
        /// <summary>
        /// Variante simplificada del texto del paso (frase más corta, lenguaje más
        /// directo), añadida en la Fase 06 para la escalada del ITS tras varios
        /// fallos consecutivos. Opcional — si está vacía, el ITS reutiliza
        /// `description` con solo el resalte visual como refuerzo adicional.
        /// </summary>
        public string simplified_description;
        public StateRef precondition;
        public StateRef postcondition;
        /// <summary>
        /// Soporte del esquema EFSM v2.1.0: transiciones alternativas equivalentes en
        /// pasos de eleccion (p. ej. Espresso O Lungo). Consumido por
        /// EfsmStateManager.StepAccepts. Vacio en EFSMs antiguos (comportamiento identico).
        /// </summary>
        public List<string> accepted_transition_refs = new List<string>();
        /// <summary>
        /// Caminos alternativos que salen de este paso (Fase 06-bis). Vacío = paso lineal
        /// (el siguiente es order+1). No vacío = punto de bifurcación: los pasos destino
        /// (to_step) son los candidatos siguientes y el usuario elige cuál realizar.
        /// </summary>
        public List<BranchDef> branches = new List<BranchDef>();
        /// <summary>Bucle acotado opcional (esquema EFSM: loop).</summary>
        public LoopDef loop;
    }

    [Serializable] public class ProcedureEntry
    {
        public string id;
        public string name;
        public List<string> requires;
        public List<string> external_preconditions;
        public string guard;
        public List<string> unlocks;
        public List<StepDef> steps;
    }

    [Serializable] public class EfsmRoot
    {
        public DeviceMeta device_meta;
        public List<ComponentEntry> components_catalog;
        public List<MachineEntry> machines;
        public List<ProcedureEntry> procedures;
    }

    /// <summary>
    /// Carga y expone el JSON del EFSM generado en la Fase 03.
    ///
    /// Nota de compatibilidad: los identificadores de este pipeline son siempre de
    /// tipo string ("lid", "scanner_glass"...), nunca enteros. El pipeline de
    /// referencia documentaba un error típico ("JSON has string IDs, parser expects
    /// int") para loaders que asumen IDs numéricos — aquí no aplica porque el
    /// esquema se diseñó ya en string desde la Fase 03, pero se deja esta nota
    /// porque es exactamente el tipo de incompatibilidad a revisar si se reutiliza
    /// este loader con un EFSM generado por otra ruta.
    /// </summary>
    public class EfsmJsonLoader : MonoBehaviour
    {
        public TextAsset jsonFile;

        public EfsmRoot Data { get; private set; }
        public event Action OnLoaded;

        private Dictionary<string, MachineEntry> _machineById;
        private Dictionary<string, ProcedureEntry> _procedureById;
        private Dictionary<string, ComponentEntry> _componentById;
        private Dictionary<string, TransitionDef> _transitionById;

        private void Start()
        {
            if (Data != null) return; // ya parseado en editor vía "Parse Now"
            if (jsonFile == null)
            {
                Debug.LogError("[EfsmJsonLoader] No se ha asignado ningún archivo JSON.");
                return;
            }
            Load();
        }

        [ContextMenu("Parse Now")]
        public void Load()
        {
            Data = JsonConvert.DeserializeObject<EfsmRoot>(jsonFile.text);

            _machineById = Data.machines.ToDictionary(m => m.id, m => m);
            _procedureById = Data.procedures.ToDictionary(p => p.id, p => p);
            _componentById = Data.components_catalog.ToDictionary(c => c.id, c => c);
            _transitionById = Data.machines
                .SelectMany(m => m.transitions)
                .ToDictionary(t => t.id, t => t);

            int timed = Data.machines
                .SelectMany(m => m.states ?? new List<StateDef>())
                .Count(s => s.timeout != null && s.timeout.after_ms.HasValue && s.timeout.after_ms.Value > 0);

            Debug.Log($"[EfsmJsonLoader] Cargado. v=1.1.0 | Componentes={Data.components_catalog.Count} " +
                      $"| Máquinas={Data.machines.Count} | Procedimientos={Data.procedures.Count} " +
                      $"| Estados temporizados={timed}");

            OnLoaded?.Invoke();
        }

        public MachineEntry MachineById(string id) =>
            _machineById != null && _machineById.TryGetValue(id, out var m) ? m : null;

        public ProcedureEntry ProcedureById(string id) =>
            _procedureById != null && _procedureById.TryGetValue(id, out var p) ? p : null;

        public ComponentEntry ComponentById(string id) =>
            _componentById != null && _componentById.TryGetValue(id, out var c) ? c : null;

        public TransitionDef GetTransition(string transitionId) =>
            _transitionById != null && _transitionById.TryGetValue(transitionId, out var t) ? t : null;

        /// <summary>Comprueba la guarda de entrada de un procedimiento contra el snapshot actual.</summary>
        public bool CheckPreconditions(ProcedureEntry proc, Dictionary<string, Dictionary<string, string>> snapshot)
        {
            if (string.IsNullOrEmpty(proc.guard)) return true;
            return GuardEvaluator.Evaluate(proc.guard, snapshot);
        }

        /// <summary>
        /// Resuelve si los procedimientos de los que depende ("requires") ya se completaron.
        /// </summary>
        public bool ResolveChain(ProcedureEntry proc, HashSet<string> completedProcedures)
        {
            if (proc.requires == null || proc.requires.Count == 0) return true;
            return proc.requires.All(completedProcedures.Contains);
        }
    }

    /// <summary>
    /// Evaluador mínimo de expresiones de guarda "machine_id.dimension eq valor",
    /// con soporte de "and" / "or" / "neq". No implementa lt/gt/lte/gte porque
    /// ningún componente de esta simulación usa dimensiones numéricas — ampliar
    /// aquí si un futuro dispositivo las necesita.
    ///
    /// Precedencia: "and" liga más fuerte que "or" (A or B and C = A or (B and C)),
    /// igual que la herramienta de autoría y validation/validate_instance.py.
    /// Por eso se separa primero por " or " y después por " and ".
    /// </summary>
    public static class GuardEvaluator
    {
        public static bool Evaluate(string expr, Dictionary<string, Dictionary<string, string>> snapshot)
        {
            if (string.IsNullOrWhiteSpace(expr)) return true;

            if (expr.Contains(" or "))
                return expr.Split(new[] { " or " }, StringSplitOptions.None)
                    .Any(e => Evaluate(e.Trim(), snapshot));

            if (expr.Contains(" and "))
                return expr.Split(new[] { " and " }, StringSplitOptions.None)
                    .All(e => Evaluate(e.Trim(), snapshot));

            var neq = expr.Contains(" neq ");
            var parts = expr.Split(new[] { neq ? " neq " : " eq " }, StringSplitOptions.None);
            if (parts.Length != 2) return false;

            var lhs = parts[0].Trim().Split('.');
            if (lhs.Length != 2) return false;
            var machineId = lhs[0];
            var dimension = lhs[1];
            var expected = parts[1].Trim().Trim('"', '\'');

            if (!snapshot.TryGetValue(machineId, out var dims)) return false;
            if (!dims.TryGetValue(dimension, out var actual)) return false;

            return neq ? actual != expected : actual == expected;
        }
    }
}
