// ============================================================================
// GenericDeviceBuilder.cs — Builder genérico data-driven (Fase 5)
// Proyecto: simulaciones MR desde manuales · Núcleo EFSM
// Ubicación sugerida: Assets/EFSM/Core/GenericDeviceBuilder.cs
//
// Sustituye a los builders escritos a mano por dispositivo
// (p. ej. HpDeskjet3050ScanBuilder). Lo específico del dispositivo pasa de
// código a datos: este script lee un device_spec.json y construye la
// jerarquía completa siguiendo las convenciones del proyecto y la guía v2.
//
// QUÉ HACE (automático, una vez, en Editor — paradigma Editor-first):
//   · DeviceRoot sin escala; pivotes de bisagra/deslizante en su eje físico.
//   · Primitivas con degradación elegante (mesh_override ausente → primitiva).
//   · Iconos planos como volúmenes de disparo sin malla (EfsmPressTrigger).
//   · Rigidbody kinemático + BoxCollider en los agarrables (regla 6.1).
//   · EfsmComponentBinder, ConstrainedMover (límites de movimiento),
//     OVRGrabbableAdapter y EfsmHighlightPulse en cada interactivo.
//   · Autogenera la checklist de Inspector por dispositivo (§6.2 guía v2
//     instanciada con los valores concretos de cada componente).
//
// QUÉ NO HACE (deliberado — queda del lado manual del Editor, receta ya
// funcional del usuario): añadir Grabbable, el transformer de agarre,
// HandGrabInteractable ni asignar sus campos. Este archivo NO referencia
// ningún tipo del SDK de Meta; la checklist generada indica esos pasos.
//
// ---------------------------------------------------------------------------
// ESTADO DE VERIFICACIÓN (v2 de este archivo)
// Alineado contra los .cs reales del núcleo (ConstrainedMover.cs y
// EfsmComponentBinder.cs subidos por el usuario, iteración de compilación 1):
//   · MovementType es un enum a NIVEL DE NAMESPACE (EFSM.MovementType), no
//     anidado en ConstrainedMover — causa del CS0426 corregido aquí.
//   · EfsmComponentBinder expone componentId ademas de machineId: el builder
//     rellena ambos.
//   · ConstrainedMover expone actionAtMax/actionAtMin (acciones EFSM al
//     soltar): el builder las rellena desde efsm_binding del spec. Sin ellas
//     el agarre no dispararia transiciones — verificar que los nombres del
//     spec coinciden con las acciones de la maquina en el EFSM JSON.
// Sigue sin compilarse en mi entorno: verificado por lectura, no por build.
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace EFSM.Devices
{
    public class GenericDeviceBuilder : MonoBehaviour
    {
        [Header("Datos del dispositivo")]
        [Tooltip("device_spec.json generado en la Fase 3-bis")]
        public TextAsset deviceSpecJson;

        [Header("Referencias del sistema EFSM")]
        public EfsmJsonLoader loader;
        public EfsmStateManager stateManager;

        [Header("Opciones")]
        [Tooltip("Aplica device_root.world_position_hint al construir si este transform está en el origen")]
        public bool applyWorldPositionHint = true;

        private DeviceSpec _spec;

        // --------------------------------------------------------------------
        // Ciclo de vida — Editor-first: Start() NO reconstruye (guía v2 §5).
        // Solo garantiza que el JSON del EFSM está cargado; los binders ya
        // serializados se auto-descubren en EfsmStateManager.
        // --------------------------------------------------------------------
        private void Start()
        {
            if (loader != null && loader.Data == null)
                loader.Load();
        }

        // ====================================================================
        // CONSTRUCCIÓN
        // ====================================================================
        [ContextMenu("Build From Device Spec")]
        public void Build()
        {
            if (deviceSpecJson == null)
            {
                Debug.LogError("[GenericDeviceBuilder] Asigna deviceSpecJson en el Inspector.");
                return;
            }

            _spec = DeviceSpec.FromJson(deviceSpecJson.text);

            ClearChildren();

            if (applyWorldPositionHint && transform.position == Vector3.zero &&
                _spec.device_root != null)
            {
                transform.position = _spec.device_root.WorldPositionHint;
            }

            // Ancla sin escala (convención: la geometría interactiva nunca se
            // parenta a un transform con escala no uniforme).
            Transform deviceRoot = new GameObject("DeviceRoot").transform;
            deviceRoot.SetParent(transform, false);
            deviceRoot.localPosition = Vector3.zero;
            deviceRoot.localRotation = Quaternion.identity;
            deviceRoot.localScale = Vector3.one;

            // Primera pasada: instancia toda la geometría y guarda la raíz de cada
            // componente por id, para poder resolver 'attach_on_complete.target_id'
            // en la segunda pasada (el target puede construirse después del origen).
            var rootsById = new Dictionary<string, Transform>();
            int built = 0, interactive = 0;
            foreach (ComponentSpec c in _spec.components)
            {
                GameObject root = BuildComponent(c, deviceRoot);
                if (root != null && !string.IsNullOrEmpty(c.id)) rootsById[c.id] = root.transform;
                built++;
                if (c.interactive) interactive++;
            }

            // Segunda pasada: cablea los pegados (cápsula → portacápsulas, taza →
            // rejilla). EfsmStickToTarget es comportamiento de runtime; aquí solo se
            // configura su 'target' y su modo de disparo según el spec.
            int attached = 0;
            foreach (ComponentSpec c in _spec.components)
            {
                if (c.attach_on_complete == null || string.IsNullOrEmpty(c.attach_on_complete.target_id))
                    continue;
                if (!rootsById.TryGetValue(c.id, out Transform src))
                    continue;
                if (!rootsById.TryGetValue(c.attach_on_complete.target_id, out Transform tgt))
                {
                    Debug.LogWarning($"[GenericDeviceBuilder] '{c.id}': attach_on_complete.target_id " +
                                     $"'{c.attach_on_complete.target_id}' no existe en el spec. No se cablea el pegado.");
                    continue;
                }
                ConfigureStickToTarget(src.gameObject, c, tgt, deviceRoot);
                attached++;
            }

            Debug.Log($"[GenericDeviceBuilder] '{_spec.device.id}' construido: " +
                      $"{built} componentes ({interactive} interactivos, {attached} con pegado). " +
                      "Recuerda: checklist de Inspector (menú contextual) y guardar la escena.");
        }

        [ContextMenu("Clear Children")]
        public void ClearChildren()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
        }

        // --------------------------------------------------------------------
        private GameObject BuildComponent(ComponentSpec c, Transform deviceRoot)
        {
            Vector3 dims = c.representation.Dimensions;
            // pivotOffset: desplazamiento del pivote respecto al centro del volumen,
            // en el marco de DeviceRoot. localPivotOffset: el mismo vector en los ejes
            // del propio componente (difiere si placement trae rotación inicial).
            Vector3 pivotOffset = ComputePivotOffset(c, dims);
            Vector3 localPivotOffset = Quaternion.Inverse(c.placement.LocalRotation) * pivotOffset;

            // Raíz del componente. Con pivote desplazado, la raíz ES el pivote
            // (convención: el pivote de bisagras/deslizantes en su eje físico)
            // y la malla cuelga como hija desplazada -pivotOffset, de modo que
            // el volumen visible queda centrado donde dice placement.
            string rootName = pivotOffset != Vector3.zero
                ? c.GameObjectName + "_Pivot"
                : c.GameObjectName;

            GameObject root = new GameObject(rootName);
            root.transform.SetParent(deviceRoot, false);
            root.transform.localPosition = c.placement.LocalPosition + pivotOffset;
            root.transform.localRotation = c.placement.LocalRotation;
            root.transform.localScale = Vector3.one;

            // ---- Geometría / volumen de disparo -----------------------------
            bool isPress = c.movement.type == MovementTypeNames.Press;
            bool isTriggerVolume = c.representation.kind == "trigger_volume" ||
                                   c.representation.flat_icon;

            if (!isTriggerVolume)
            {
                GameObject visual = CreateVisual(c, dims);
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = -localPivotOffset;
                visual.transform.localRotation = Quaternion.identity;
            }

            // ---- Collider en la raíz, cubriendo el volumen visible ----------
            // Un componente 'press' con malla propia (p. ej. el cristal del
            // escáner: se toca para colocar el documento) conserva su visual
            // pero su collider actúa como volumen de disparo.
            BoxCollider box = root.AddComponent<BoxCollider>();
            box.center = -localPivotOffset;
            box.size = dims;
            box.isTrigger = isTriggerVolume || isPress;

            // ---- Física y vinculación EFSM ----------------------------------
            if (c.interactive && !isTriggerVolume && !isPress)
            {
                // Regla 6.1: todo agarrable con Rigidbody kinemático sin gravedad.
                Rigidbody rb = root.AddComponent<Rigidbody>();
                rb.isKinematic = c.xr_recipe?.rigidbody == null || c.xr_recipe.rigidbody.is_kinematic;
                rb.useGravity = c.xr_recipe?.rigidbody != null && c.xr_recipe.rigidbody.use_gravity;
                if (!rb.isKinematic)
                    Debug.LogWarning($"[GenericDeviceBuilder] '{c.id}': el spec pide Rigidbody NO " +
                                     "kinemático, contra la regla 6.1 de la guía. Revisar.");
            }

            if (!string.IsNullOrEmpty(c.machine_id))
            {
                EfsmComponentBinder binder = root.AddComponent<EfsmComponentBinder>();
                binder.componentId = c.id;      // el loader lo re-inicializa en Play (Initialize)
                binder.machineId = c.machine_id;
            }

            bool isAttach = c.movement.type == MovementTypeNames.Attach;

            if (c.interactive && !isTriggerVolume && IsGrabbableMovement(c.movement.type))
            {
                ConfigureConstrainedMover(root, c, dims);
                root.AddComponent<OVRGrabbableAdapter>();   // valida la receta en runtime
                root.AddComponent<EfsmHighlightPulse>();    // resalte del ITS
            }
            else if (c.interactive && isAttach)
            {
                // Soltar-y-pegar sin recorrido constreñido: el objeto es agarrable
                // (se coge y se lleva al receptáculo) pero no lleva ConstrainedMover;
                // el pegado lo hace EfsmStickToTarget, cableado en la 2ª pasada.
                root.AddComponent<OVRGrabbableAdapter>();
                root.AddComponent<EfsmHighlightPulse>();
            }
            else if (c.interactive && isPress)
            {
                EfsmPressTrigger press = root.AddComponent<EfsmPressTrigger>();
                // El nombre de la acción viene del spec; si falta, se conserva
                // el valor por defecto del componente y se avisa.
                if (c.efsm_binding != null && !string.IsNullOrEmpty(c.efsm_binding.press_action))
                    press.action = c.efsm_binding.press_action;
                else
                    Debug.LogWarning($"[GenericDeviceBuilder] '{c.id}': componente press sin " +
                                     "efsm_binding.press_action en el spec; se usa el valor por defecto del trigger.");
                root.AddComponent<EfsmHighlightPulse>();

                // Feedback "blink": el pulso se dispara desde OnMachineStateChanged.
                if (c.movement.feedback == FeedbackNames.Blink)
                {
                    press.blinkOnStateChange = true;
                    Debug.Log($"[GenericDeviceBuilder] '{c.id}': feedback blink activado.");
                }
            }

            return root;
        }

        // El pegado (attach) también puede acompañar a un movimiento con recorrido
        // (deslizar y pegar): si el componente es grabbable Y trae attach_on_complete,
        // la 2ª pasada añade igualmente el EfsmStickToTarget encima del mover.
        private static void ConfigureStickToTarget(GameObject root, ComponentSpec c, Transform target, Transform deviceRoot)
        {
            EfsmStickToTarget stick = root.GetComponent<EfsmStickToTarget>();
            if (stick == null) stick = root.AddComponent<EfsmStickToTarget>();
            stick.target = target;
            stick.snapToTargetPose = c.attach_on_complete.snap_to_end_pose;

            // Pose al pegarse: la pose final de la trayectoria que fijó el autor
            // (marco de DeviceRoot), expresada relativa al target. Sin trayectoria,
            // el objeto se coloca en el origen del target (offset cero).
            if (stick.snapToTargetPose && c.trajectory?.end_pose != null && c.trajectory.end_pose.HasPosition)
            {
                Vector3 endWorld = deviceRoot.TransformPoint(c.trajectory.end_pose.Position);
                stick.localPositionOffset = target.InverseTransformPoint(endWorld);
                Quaternion endRot = deviceRoot.rotation * c.placement.LocalRotation;
                stick.localEulerOffset = (Quaternion.Inverse(target.rotation) * endRot).eulerAngles;
            }

            // Modo de disparo: si el objeto tiene ConstrainedMover (deslizar y pegar),
            // se pega al completar el recorrido; si no (soltar y pegar), queda en
            // Manual y lo dispara OVRGrabbableAdapter/ITS al soltar (el usuario cablea
            // la llamada a Attach() en el Inspector, según la checklist).
            bool hasMover = root.GetComponent<ConstrainedMover>() != null;
            stick.trigger = hasMover
                ? EfsmStickToTarget.Trigger.OnMoverComplete
                : EfsmStickToTarget.Trigger.Manual;

            Debug.Log($"[GenericDeviceBuilder] '{c.id}': pegado a '{c.attach_on_complete.target_id}' " +
                      $"(modo {stick.trigger}).");
        }

        private static bool IsGrabbableMovement(string type) =>
            type == MovementTypeNames.Hinge ||
            type == MovementTypeNames.SlideLinear ||
            type == MovementTypeNames.RotateAxis;

        // --------------------------------------------------------------------
        private GameObject CreateVisual(ComponentSpec c, Vector3 dims)
        {
            // Degradación elegante: si hay mesh_override pero el asset no está
            // (Fase 4 pendiente), se cae a la primitiva declarada.
            // La sustitución por malla real se hace en Editor (guía v2 §"malla
            // real"), no aquí: este builder solo garantiza la primitiva.
            PrimitiveType prim = PrimitiveType.Cube;
            switch (c.representation.primitive)
            {
                case "cylinder": prim = PrimitiveType.Cylinder; break;
                case "sphere":   prim = PrimitiveType.Sphere;   break;
                case "plane":    prim = PrimitiveType.Quad;     break;
            }

            GameObject visual = GameObject.CreatePrimitive(prim);
            visual.name = c.GameObjectName + "_Visual";
            visual.transform.localScale = dims;

            // El collider vive en la raíz del componente; el de la primitiva sobra.
            Collider primCol = visual.GetComponent<Collider>();
            if (primCol != null)
            {
                if (Application.isPlaying) Destroy(primCol);
                else DestroyImmediate(primCol);
            }

            Color color = c.interactive
                ? new Color(0.70f, 0.82f, 0.92f)   // interactivo: azul claro
                : c.representation.ColorHint;
            SetColorEditSafe(visual.GetComponent<Renderer>(), color);
            return visual;
        }

        /// <summary>
        /// Asigna color sin instanciar .material en edit mode (evita el
        /// material leak documentado en el historial de depuración, caso 2).
        /// </summary>
        private static void SetColorEditSafe(Renderer r, Color color)
        {
            if (r == null) return;
            var mat = new Material(Shader.Find("Standard")) { color = color };
            r.sharedMaterial = mat;
        }

        // --------------------------------------------------------------------
        /// <summary>
        /// Desplazamiento del pivote respecto al centro del volumen visible, en el
        /// marco de DeviceRoot. Con rotación inicial nula coincide con los ejes del
        /// componente (caso de las instancias del estudio).
        /// </summary>
        private static Vector3 ComputePivotOffset(ComponentSpec c, Vector3 dims)
        {
            PivotSpec p = c.placement.pivot;
            if (p == null || p.mode == "center" || string.IsNullOrEmpty(p.mode))
                return Vector3.zero;

            Quaternion rot = c.placement.LocalRotation;

            // Pivote calculado por la herramienta a partir de las poses de la
            // trayectoria: posición absoluta del eje en DeviceRoot.
            if (p.mode == "explicit" && p.HasLocalPosition)
                return p.LocalPosition - c.placement.LocalPosition;

            if (p.mode == "explicit" && p.offset_m != null && p.offset_m.Length == 3)
                return rot * new Vector3(p.offset_m[0], p.offset_m[1], p.offset_m[2]);

            if (p.mode == "edge")
            {
                // Convención de ejes locales de DeviceRoot: +z = frente del
                // dispositivo (hacia el usuario), +x = derecha, +y = arriba.
                switch (p.edge)
                {
                    case "front":  return rot * new Vector3(0, 0,  dims.z * 0.5f);
                    case "back":   return rot * new Vector3(0, 0, -dims.z * 0.5f);
                    case "left":   return rot * new Vector3(-dims.x * 0.5f, 0, 0);
                    case "right":  return rot * new Vector3( dims.x * 0.5f, 0, 0);
                    case "top":    return rot * new Vector3(0,  dims.y * 0.5f, 0);
                    case "bottom": return rot * new Vector3(0, -dims.y * 0.5f, 0);
                }
            }
            Debug.LogWarning($"[GenericDeviceBuilder] '{c.id}': pivot '{p.mode}/{p.edge}' no reconocido; uso centro.");
            return Vector3.zero;
        }

        // --------------------------------------------------------------------
        private static void ConfigureConstrainedMover(GameObject root, ComponentSpec c, Vector3 dims)
        {
            ConstrainedMover mover = root.AddComponent<ConstrainedMover>();

            Vector3 axis = AxisFromName(c.movement.axis);
            if (c.movement.invert) axis = -axis;   // ajuste de progreso (guía v2 §6.2)

            switch (c.movement.type)
            {
                case MovementTypeNames.Hinge:
                    mover.movementType = MovementType.FlipHinge;
                    mover.localRotationAxis = axis;
                    mover.angleMin = c.movement.range.min;
                    mover.angleMax = c.movement.range.max;
                    break;

                case MovementTypeNames.RotateAxis:
                    mover.movementType = MovementType.RotateAxis;
                    mover.localRotationAxis = axis;
                    mover.angleMin = c.movement.range.min;
                    mover.angleMax = c.movement.range.max;
                    break;

                case MovementTypeNames.SlideLinear:
                    mover.movementType = SlideTypeFromAxis(c.movement.axis);
                    mover.localAxis = axis;
                    mover.limitMin = c.movement.range.min;
                    mover.limitMax = c.movement.range.max;
                    break;
            }

            if (c.efsm_binding != null)
            {
                mover.openThreshold = c.efsm_binding.open_threshold;
                mover.closedThreshold = c.efsm_binding.closed_threshold;
                // Acciones EFSM disparadas al soltar (deben existir como
                // acciones de la maquina machine_id en el EFSM JSON; sin ellas
                // el agarre mueve el objeto pero no dispara transiciones).
                if (!string.IsNullOrEmpty(c.efsm_binding.action_at_max))
                    mover.actionAtMax = c.efsm_binding.action_at_max;
                if (!string.IsNullOrEmpty(c.efsm_binding.action_at_min))
                    mover.actionAtMin = c.efsm_binding.action_at_min;
            }
            mover.snapOnRelease = true;
        }

        private static Vector3 AxisFromName(string axis)
        {
            switch (axis)
            {
                case "up":      return Vector3.up;
                case "forward": return Vector3.forward;
                case "right":
                default:        return Vector3.right;
            }
        }

        private static MovementType SlideTypeFromAxis(string axis)
        {
            switch (axis)
            {
                case "up":      return MovementType.SlideVertical;
                case "forward": return MovementType.SlideDepth;
                default:        return MovementType.SlideHorizontal;
            }
        }

        // ====================================================================
        // CHECKLIST DE INSPECTOR AUTOGENERADA (§6.2 guía v2 por componente)
        // ====================================================================
        [ContextMenu("Generate Inspector Checklist")]
        public void GenerateInspectorChecklist()
        {
            if (_spec == null)
            {
                if (deviceSpecJson == null)
                {
                    Debug.LogError("[GenericDeviceBuilder] Asigna deviceSpecJson primero.");
                    return;
                }
                _spec = DeviceSpec.FromJson(deviceSpecJson.text);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# Checklist de Inspector — {_spec.device.display_name}");
            sb.AppendLine();
            sb.AppendLine($"Generada automáticamente desde `{_spec.device.id}` (spec v{_spec.spec_version}).");
            sb.AppendLine("Pasos manuales de la receta XR (guía v2 §6) instanciados por componente.");
            sb.AppendLine("El builder ya ha dejado: BoxCollider, Rigidbody kinemático, binder,");
            sb.AppendLine("ConstrainedMover configurado, OVRGrabbableAdapter y EfsmHighlightPulse.");
            sb.AppendLine();

            int n = 0;
            foreach (ComponentSpec c in _spec.components)
            {
                if (!c.interactive || !IsGrabbableMovement(c.movement.type)) continue;
                n++;
                string goName = (ComputePivotOffset(c, c.representation.Dimensions) != Vector3.zero)
                    ? c.GameObjectName + "_Pivot" : c.GameObjectName;
                string transformer = c.xr_recipe?.transformer ?? "OneGrabRotateTransformer";
                float min = c.xr_recipe?.constraints?.min ?? c.movement.range.min;
                float max = c.xr_recipe?.constraints?.max ?? c.movement.range.max;
                string units = c.movement.range?.units ?? "degrees";
                string provider = c.xr_recipe?.movement_provider ?? "MoveFromTargetProvider";

                sb.AppendLine($"## {n}. `{goName}`  (movimiento: {c.movement.type}, eje {c.movement.axis}, {min}–{max} {units})");
                sb.AppendLine();
                sb.AppendLine("1. Verificar Rigidbody: **Is Kinematic ✓**, Use Gravity ✗ (el builder lo deja así).");
                sb.AppendLine("2. Añadir **Grabbable**.");
                sb.AppendLine($"3. Añadir **{transformer}**: Rotation Axis = **{AxisLabel(c.movement.axis)}**, " +
                              $"Constraints Min **{min}** / Max **{max}**, Pivot Transform **vacío**.");
                if (c.movement.type == MovementTypeNames.SlideLinear)
                    sb.AppendLine("   ⚠ Componente deslizante: la receta validada del proyecto cubre bisagras. " +
                                  "Verificar en la doc oficial de Meta (201.0.0) el transformer de traslación " +
                                  "equivalente antes de configurarlo.");
                sb.AppendLine("4. En **Grabbable → One Grab Transformer**: arrastrar el transformer del paso 3.");
                sb.AppendLine("5. Añadir **HandGrabInteractable**: campo Rigidbody → el Rigidbody de este objeto; " +
                              $"Movement provider (si existe) → **{provider}**; Pointable Element solo como " +
                              "fallback si agarra pero no se mueve.");
                sb.AppendLine($"6. En ConstrainedMover, si el progreso corre al revés al probar: campo invert del " +
                              $"spec o invertir el signo del eje (ajuste, no bug).");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine($"Componentes agarrables: {n}. Al terminar: **File → Save Scene** " +
                          "(la escena serializada es la fuente de verdad; reconstruir con el builder " +
                          "borra la jerarquía y obliga a repetir esta checklist).");

            string dir = Path.Combine(Application.dataPath, "EFSM", "Generated");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"Checklist_{_spec.device.id}.md");
            File.WriteAllText(path, sb.ToString());
#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
            Debug.Log($"[GenericDeviceBuilder] Checklist generada: {path}");
        }

        private static string AxisLabel(string axis)
        {
            switch (axis)
            {
                case "up":      return "Up (eje Y local)";
                case "forward": return "Forward (eje Z local)";
                default:        return "Right (eje X local)";
            }
        }
    }
}
