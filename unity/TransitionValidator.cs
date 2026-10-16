using System;
using System.Linq;
using UnityEngine;

namespace EFSM
{
    /// <summary>
    /// Verifica al cargar el EFSM que todo transition_ref referenciado en los
    /// procedimientos existe en machines, y que toda guarda referencia una
    /// dimension real de components_catalog. Es una comprobacion de integridad,
    /// no bloquea la ejecucion — solo registra errores en consola.
    /// </summary>
    [RequireComponent(typeof(EfsmJsonLoader))]
    public class TransitionValidator : MonoBehaviour
    {
        private EfsmJsonLoader _loader;

        private void OnEnable()
        {
            _loader = GetComponent<EfsmJsonLoader>();
            _loader.OnLoaded += Validate;
        }

        private void OnDisable()
        {
            if (_loader != null) _loader.OnLoaded -= Validate;
        }

        private void Validate()
        {
            var allTransitionIds = _loader.Data.machines
                .SelectMany(m => m.transitions.Select(t => t.id))
                .ToHashSet();

            var allDimensionsByMachine = _loader.Data.components_catalog
                .ToDictionary(c => c.machine_id, c => c.dimensions.Select(d => d.name).ToHashSet());

            var errors = 0;

            foreach (var proc in _loader.Data.procedures)
            {
                foreach (var step in proc.steps)
                {
                    if (step.external_step) continue;
                    if (string.IsNullOrEmpty(step.transition_ref)) continue;
                    if (!allTransitionIds.Contains(step.transition_ref))
                    {
                        Debug.LogError($"[TransitionValidator] '{proc.id}' paso {step.order}: " +
                                        $"transition_ref '{step.transition_ref}' no existe en machines.");
                        errors++;
                    }
                }
            }

            foreach (var machine in _loader.Data.machines)
            {
                foreach (var t in machine.transitions)
                {
                    if (string.IsNullOrEmpty(t.guard)) continue;
                    foreach (var clause in t.guard.Split(new[] { " and ", " or " }, StringSplitOptions.None))
                    {
                        var lhs = clause.Trim().Split(' ')[0];
                        var bits = lhs.Split('.');
                        if (bits.Length != 2) continue;
                        if (!allDimensionsByMachine.TryGetValue(bits[0], out var dims) || !dims.Contains(bits[1]))
                        {
                            Debug.LogError($"[TransitionValidator] Guarda invalida en '{t.id}': " +
                                            $"'{bits[0]}.{bits[1]}' no es una dimension conocida.");
                            errors++;
                        }
                    }
                }
            }

            Debug.Log(errors == 0
                ? "[TransitionValidator] Integridad del EFSM verificada sin errores."
                : $"[TransitionValidator] {errors} problema(s) de integridad encontrados.");
        }
    }
}
