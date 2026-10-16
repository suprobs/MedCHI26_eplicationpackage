using System;
using System.Collections.Generic;
using UnityEngine;

namespace EFSM
{
    /// <summary>
    /// Vincula un GameObject de la escena con una entrada del components_catalog
    /// del EFSM. Mantiene el valor actual de cada dimensión y notifica cambios.
    /// </summary>
    public class EfsmComponentBinder : MonoBehaviour
    {
        public string componentId;
        public string machineId;

        public event Action<string, string, string> OnDimensionChanged; // dimension, prev, next

        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public void Initialize(ComponentEntry component)
        {
            componentId = component.id;
            machineId = component.machine_id;
            _values.Clear();
            foreach (var dim in component.dimensions)
                _values[dim.name] = dim.initial;

            Debug.Log($"[EfsmComponentBinder] '{componentId}' vinculado. Dimensiones: [{string.Join(", ", _values.Keys)}]");
        }

        public void SetDimensionValue(string dimension, string value)
        {
            _values.TryGetValue(dimension, out var prev);
            if (prev == value) return;

            _values[dimension] = value;
            Debug.Log($"[EfsmComponentBinder] '{componentId}' | {dimension}: {prev} -> {value}");
            OnDimensionChanged?.Invoke(dimension, prev, value);
        }

        public string GetDimensionValue(string dimension) =>
            _values.TryGetValue(dimension, out var v) ? v : null;

        public IReadOnlyDictionary<string, string> GetDimensionValues() => _values;

        /// <summary>
        /// Comprueba si el EfsmStateManager autoriza una acción sobre este componente
        /// dada una expresión de guarda, consultando el snapshot global de estado.
        /// </summary>
        public bool AllowsMovement(string requiredGuard)
        {
            var manager = EfsmStateManager.Instance;
            if (manager == null || string.IsNullOrEmpty(requiredGuard)) return true;
            return GuardEvaluator.Evaluate(requiredGuard, manager.GetSnapshot());
        }
    }
}
