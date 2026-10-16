// ============================================================================
// EfsmVisualReplacer.cs — Sustitución del visual primitivo por malla real (Fase 4)
// Proyecto: simulaciones MR desde manuales · Núcleo EFSM
// Ubicación sugerida: Assets/EFSM/Core/EfsmVisualReplacer.cs
//
// QUÉ HACE
//   Se añade a la RAÍZ de un componente construido por GenericDeviceBuilder
//   (p. ej. "Cup", "Lid_Pivot") y sustituye su primitiva "<Nombre>_Visual"
//   por un prefab/modelo 3D personalizado, EN EDIT MODE (paradigma
//   Editor-first, vía ContextMenu o botones del Inspector).
//
//   · Escala la malla para que su bounding box coincida con las dimensiones
//     del primitivo original (las del device_spec) — opcionalmente
//     conservando proporciones.
//   · Centra el volumen de la malla exactamente donde estaba el primitivo
//     (corrige pivotes descentrados del modelo importado).
//   · La primitiva NO se borra: se desactiva → botón "Revertir" disponible.
//   · Ajuste fino desde el Inspector: offsets de posición/rotación/escala
//     con botones de giro rápido ±90° por eje (caso típico: modelo importado
//     con el eje "up" cambiado).
//
// QUÉ NO TOCA (deliberado)
//   El BoxCollider, Rigidbody, binder, ConstrainedMover, adapter y triggers
//   viven en la raíz del componente y se dimensionan desde el spec: la
//   interacción y la semántica EFSM son idénticas antes y después del
//   reemplazo (degradación elegante en ambos sentidos). Este script no
//   referencia ningún tipo del SDK de Meta.
//
// USO
//   1. Añadir EfsmVisualReplacer a la raíz del componente (Cup, Lid_Pivot…).
//   2. Arrastrar el prefab/modelo al campo "Custom Model".
//   3. Botón "Sustituir visual" (o ContextMenu). Ajustar con los botones
//      de rotación / los offsets si la orientación no es la esperada.
//   4. Guardar la escena (la escena serializada es la fuente de verdad;
//      un Rebuild del GenericDeviceBuilder borra la jerarquía y habrá que
//      repetir la sustitución — mismo régimen que la checklist XR).
//
// ESTADO DE VERIFICACIÓN: escrito contra la jerarquía real que genera
// GenericDeviceBuilder (raíz + hijo "<Nombre>_Visual" desplazado -pivotOffset).
// No compilado en este entorno; verificar en Unity 2022.3.
// ============================================================================

using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace EFSM
{
    [DisallowMultipleComponent]
    public class EfsmVisualReplacer : MonoBehaviour
    {
        [Header("Modelo personalizado")]
        [Tooltip("Prefab o modelo (FBX/OBJ importado) que sustituirá al visual primitivo.")]
        public GameObject customModel;

        [Tooltip("Si está activo, escala uniforme (encaja dentro de las dimensiones sin deformar). " +
                 "Si no, escala no uniforme para ocupar exactamente el volumen del primitivo.")]
        public bool preserveAspectRatio = true;

        [Header("Ajuste fino (aplicado sobre la pose del primitivo)")]
        public Vector3 positionOffset = Vector3.zero;
        public Vector3 rotationOffsetEuler = Vector3.zero;
        [Tooltip("Multiplicador adicional sobre la escala calculada automáticamente.")]
        public float scaleMultiplier = 1f;

        // Serializado para sobrevivir a recargas de dominio y guardado de escena.
        [SerializeField, HideInInspector] private GameObject _primitiveVisual;
        [SerializeField, HideInInspector] private GameObject _customHolder;
        [SerializeField, HideInInspector] private Vector3 _visualLocalPosition;
        [SerializeField, HideInInspector] private Vector3 _targetDimensions;
        [SerializeField, HideInInspector] private Vector3 _autoScale = Vector3.one;
        [SerializeField, HideInInspector] private Vector3 _meshCenterLocal;

        public bool IsReplaced => _customHolder != null;

        // ====================================================================
        // SUSTITUCIÓN
        // ====================================================================
        [ContextMenu("Sustituir visual por modelo personalizado")]
        public void ReplaceVisual()
        {
            if (customModel == null)
            {
                Debug.LogError($"[EfsmVisualReplacer] '{name}': asigna 'Custom Model' antes de sustituir.", this);
                return;
            }

            if (!FindPrimitiveVisual()) return;

            // Dimensiones objetivo = escala local del primitivo (el builder pone
            // localScale = dimensions_m del spec). El collider de la raíz ya
            // cubre ese volumen y no se toca.
            _visualLocalPosition = _primitiveVisual.transform.localPosition;
            _targetDimensions = _primitiveVisual.transform.localScale;

            RemoveCustomHolder(); // idempotente: re-sustituir limpia el anterior

            // Instancia temporal a escala 1 para medir el bounding box combinado.
            GameObject instance = InstantiateModel();
            if (instance == null) return;

            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            if (!TryComputeBounds(instance, out Bounds b))
            {
                Debug.LogError($"[EfsmVisualReplacer] '{name}': el modelo no tiene Renderers; no se puede medir.", this);
                DestroyEditSafe(instance);
                return;
            }

            // Escala automática: volumen del modelo → dimensiones del primitivo.
            Vector3 s = new Vector3(
                b.size.x > 1e-5f ? _targetDimensions.x / b.size.x : 1f,
                b.size.y > 1e-5f ? _targetDimensions.y / b.size.y : 1f,
                b.size.z > 1e-5f ? _targetDimensions.z / b.size.z : 1f);
            if (preserveAspectRatio)
            {
                float u = Mathf.Min(s.x, Mathf.Min(s.y, s.z));
                s = new Vector3(u, u, u);
            }
            _autoScale = s;
            _meshCenterLocal = b.center; // offset del centro del volumen respecto al pivote del modelo

            // Holder: recibe pose + offsets de usuario. El modelo cuelga dentro,
            // corregido para que su CENTRO DE VOLUMEN caiga en el origen del holder.
            _customHolder = new GameObject(_primitiveVisual.name.Replace("_Visual", "") + "_CustomVisual");
            _customHolder.transform.SetParent(transform, false);

            instance.transform.SetParent(_customHolder.transform, false);
            ApplyTransforms();

            // Colliders del modelo importado sobran: el de la raíz manda.
            foreach (Collider col in instance.GetComponentsInChildren<Collider>())
                DestroyEditSafe(col);

            _primitiveVisual.SetActive(false);

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Undo.RegisterCreatedObjectUndo(_customHolder, "Replace EFSM Visual");
                EditorUtility.SetDirty(this);
                EditorUtility.SetDirty(gameObject);
            }
#endif
            Debug.Log($"[EfsmVisualReplacer] '{name}': visual sustituido por '{customModel.name}' " +
                      $"(escala auto {_autoScale.x:F3},{_autoScale.y:F3},{_autoScale.z:F3}). " +
                      "Ajusta orientación con los botones si es necesario y GUARDA LA ESCENA.", this);
        }

        [ContextMenu("Revertir a primitiva")]
        public void RevertToPrimitive()
        {
            RemoveCustomHolder();
            if (_primitiveVisual != null) _primitiveVisual.SetActive(true);
            else FindPrimitiveVisual(); // por si la referencia se perdió
            if (_primitiveVisual != null) _primitiveVisual.SetActive(true);
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(gameObject);
#endif
            Debug.Log($"[EfsmVisualReplacer] '{name}': revertido al visual primitivo.", this);
        }

        // ====================================================================
        // AJUSTE FINO
        // ====================================================================
        /// <summary>Reaplica pose y escala del holder desde los offsets actuales.</summary>
        public void ApplyTransforms()
        {
            if (_customHolder == null) return;

            _customHolder.transform.localPosition = _visualLocalPosition + positionOffset;
            _customHolder.transform.localRotation = Quaternion.Euler(rotationOffsetEuler);
            _customHolder.transform.localScale = Vector3.one;

            if (_customHolder.transform.childCount > 0)
            {
                Transform model = _customHolder.transform.GetChild(0);
                Vector3 finalScale = _autoScale * Mathf.Max(0.0001f, scaleMultiplier);
                model.localScale = finalScale;
                // Centrado: el centro de volumen del modelo al origen del holder.
                model.localPosition = -Vector3.Scale(_meshCenterLocal, finalScale);
                model.localRotation = Quaternion.identity;
            }
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(_customHolder);
#endif
        }

        public void RotateBy(Vector3 eulerDelta)
        {
            rotationOffsetEuler += eulerDelta;
            // Normaliza a [-180, 180] por legibilidad del Inspector.
            rotationOffsetEuler = new Vector3(
                Mathf.DeltaAngle(0, rotationOffsetEuler.x),
                Mathf.DeltaAngle(0, rotationOffsetEuler.y),
                Mathf.DeltaAngle(0, rotationOffsetEuler.z));
            ApplyTransforms();
        }

        private void OnValidate()
        {
            // Edición en vivo: cambiar un offset en el Inspector actualiza la pose.
            if (_customHolder != null) ApplyTransforms();
        }

        // ====================================================================
        // INTERNOS
        // ====================================================================
        private bool FindPrimitiveVisual()
        {
            if (_primitiveVisual != null) return true;
            foreach (Transform child in transform)
            {
                if (child.name.EndsWith("_Visual"))
                {
                    _primitiveVisual = child.gameObject;
                    return true;
                }
            }
            Debug.LogError($"[EfsmVisualReplacer] '{name}': no se encontró ningún hijo '*_Visual'. " +
                           "Este componente debe añadirse a la raíz de un componente construido por " +
                           "GenericDeviceBuilder (los trigger_volume/flat_icon no tienen visual que sustituir).", this);
            return false;
        }

        private GameObject InstantiateModel()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && PrefabUtility.IsPartOfPrefabAsset(customModel))
            {
                // Mantiene el vínculo con el prefab (actualizable si el asset cambia).
                return (GameObject)PrefabUtility.InstantiatePrefab(customModel);
            }
#endif
            return Instantiate(customModel);
        }

        private static bool TryComputeBounds(GameObject go, out Bounds bounds)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            bounds = default;
            if (renderers.Length == 0) return false;
            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            // El objeto está en el origen con rotación identidad y escala 1:
            // los bounds mundiales equivalen a los locales del modelo.
            return true;
        }

        private void RemoveCustomHolder()
        {
            if (_customHolder == null) return;
            DestroyEditSafe(_customHolder);
            _customHolder = null;
        }

        private static void DestroyEditSafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(EfsmVisualReplacer))]
    [CanEditMultipleObjects]
    public class EfsmVisualReplacerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var r = (EfsmVisualReplacer)target;

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = r.customModel != null;
                if (GUILayout.Button(r.IsReplaced ? "Re-sustituir visual" : "Sustituir visual", GUILayout.Height(28)))
                    foreach (var t in targets) ((EfsmVisualReplacer)t).ReplaceVisual();
                GUI.enabled = r.IsReplaced;
                if (GUILayout.Button("Revertir a primitiva", GUILayout.Height(28)))
                    foreach (var t in targets) ((EfsmVisualReplacer)t).RevertToPrimitive();
                GUI.enabled = true;
            }

            if (!r.IsReplaced) return;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Giro rápido (±90°)", EditorStyles.boldLabel);
            DrawRotRow("X", Vector3.right);
            DrawRotRow("Y", Vector3.up);
            DrawRotRow("Z", Vector3.forward);

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Los offsets del Inspector se aplican en vivo. Al terminar: File → Save Scene. " +
                "Un Rebuild del GenericDeviceBuilder borra la jerarquía: habrá que repetir la sustitución.",
                MessageType.Info);
        }

        private void DrawRotRow(string label, Vector3 axis)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Eje {label}", GUILayout.Width(50));
                if (GUILayout.Button($"-90°")) Rotate(-90f * axis);
                if (GUILayout.Button($"+90°")) Rotate(+90f * axis);
            }
        }

        private void Rotate(Vector3 delta)
        {
            foreach (var t in targets)
            {
                var rep = (EfsmVisualReplacer)t;
                Undo.RecordObject(rep, "Rotate custom visual");
                rep.RotateBy(delta);
                EditorUtility.SetDirty(rep);
            }
        }
    }
#endif
}
