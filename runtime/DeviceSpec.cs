// ============================================================================
// DeviceSpec.cs — Modelos de datos del device_spec.json (Fase 3-bis)
// Proyecto: simulaciones MR desde manuales · Núcleo EFSM
// Ubicación sugerida: Assets/EFSM/Core/DeviceSpec.cs
//
// Sin dependencias del SDK de Meta. Requiere Newtonsoft Json
// (com.unity.nuget.newtonsoft-json), ya presente en el proyecto.
//
// Esquema: ESPECIFICACION_FASE_2BIS.md §6 (spec_version 1.0).
// Los nombres de campo replican el JSON en snake_case a propósito, para que
// la deserialización sea directa y el spec siga siendo legible/diferenciable
// en control de versiones sin capa de mapeo.
// ============================================================================

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace EFSM.Devices
{
    [Serializable]
    public class DeviceSpec
    {
        public string spec_version;
        public DeviceInfoSpec device;
        public DeviceRootSpec device_root;
        public List<ComponentSpec> components = new List<ComponentSpec>();

        public static DeviceSpec FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("[DeviceSpec] JSON vacío.");
            DeviceSpec spec = JsonConvert.DeserializeObject<DeviceSpec>(json);
            spec.Validate();
            return spec;
        }

        /// <summary>
        /// Validación formal mínima (subconjunto del validador V4 ejecutable
        /// dentro de Unity). No sustituye a la validación de la fase de autoría.
        /// </summary>
        public void Validate()
        {
            if (device == null || string.IsNullOrEmpty(device.id))
                throw new Exception("[DeviceSpec] Falta device.id.");
            if (components == null || components.Count == 0)
                throw new Exception("[DeviceSpec] La lista de componentes está vacía.");

            var seenIds = new HashSet<string>();
            foreach (var c in components)
            {
                if (string.IsNullOrEmpty(c.id))
                    throw new Exception("[DeviceSpec] Componente sin id.");
                if (!seenIds.Add(c.id))
                    throw new Exception($"[DeviceSpec] id duplicado: '{c.id}'.");
                if (c.representation == null)
                    throw new Exception($"[DeviceSpec] '{c.id}' sin representation.");
                if (c.placement == null)
                    throw new Exception($"[DeviceSpec] '{c.id}' sin placement.");
                if (c.movement == null)
                    throw new Exception($"[DeviceSpec] '{c.id}' sin movement.");
                if (c.interactive && string.IsNullOrEmpty(c.machine_id))
                    throw new Exception($"[DeviceSpec] '{c.id}' es interactivo pero no tiene machine_id.");
                if (c.movement.type == MovementTypeNames.Hinge ||
                    c.movement.type == MovementTypeNames.RotateAxis ||
                    c.movement.type == MovementTypeNames.SlideLinear)
                {
                    if (c.movement.range == null)
                        throw new Exception($"[DeviceSpec] '{c.id}' móvil sin movement.range.");
                    if (string.IsNullOrEmpty(c.movement.axis))
                        throw new Exception($"[DeviceSpec] '{c.id}' móvil sin movement.axis.");
                }
                if (c.movement.type == MovementTypeNames.Attach &&
                    (c.attach_on_complete == null || string.IsNullOrEmpty(c.attach_on_complete.target_id)))
                    throw new Exception($"[DeviceSpec] '{c.id}' con movement=attach sin attach_on_complete.target_id.");
            }
        }
    }

    [Serializable]
    public class DeviceInfoSpec
    {
        public string id;
        public string display_name;
        public string source_manual;
        public string efsm_ref;
        public string selection_ref;
    }

    [Serializable]
    public class DeviceRootSpec
    {
        public float[] world_position_hint;
        public string note;

        public Vector3 WorldPositionHint =>
            world_position_hint != null && world_position_hint.Length == 3
                ? new Vector3(world_position_hint[0], world_position_hint[1], world_position_hint[2])
                : Vector3.zero;
    }

    [Serializable]
    public class ComponentSpec
    {
        public string id;
        public string machine_id;      // null en geometría no vinculada (chasis)
        public bool interactive;
        public RepresentationSpec representation;
        public PlacementSpec placement;
        public MovementSpec movement;
        public XrRecipeSpec xr_recipe;         // informativo: alimenta la checklist
        public EfsmBindingSpec efsm_binding;
        public Dictionary<string, string> provenance;

        // Pegado a otro componente al completar el gesto (cápsula → portacápsulas,
        // taza → rejilla). Lo resuelve GenericDeviceBuilder en una segunda pasada
        // (necesita que el target ya exista) cableando un EfsmStickToTarget.
        // Presente tanto en el modo "trayectoria + attach" (deslizar y pegar) como
        // en el modo movement.type = "attach" (soltar y pegar, sin recorrido).
        public AttachSpec attach_on_complete;

        // Marca informativa: geometría creada/rehecha por el usuario en la authoring
        // tool (no proviene directamente del manual). No cambia el build; queda en el
        // spec para trazabilidad y para el manifest de integridad de escena.
        public bool user_created;
        public string display_name;

        /// <summary>Nombre de GameObject legible: "lid" → "Lid".</summary>
        public string GameObjectName
        {
            get
            {
                if (string.IsNullOrEmpty(id)) return "Unnamed";
                string[] parts = id.Split('_');
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i].Length > 0)
                        parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
                return string.Join("_", parts);
            }
        }
    }

    [Serializable]
    public class RepresentationSpec
    {
        public string kind;            // primitive | trigger_volume | mesh
        public string primitive;       // cube | cylinder | sphere | plane (degradación elegante)
        public float[] dimensions_m;   // [x, y, z] en metros
        public string mesh_override;   // ruta de malla real (Fase 4); null → primitiva
        public bool flat_icon;
        public float[] color_hint;     // [r, g, b] 0–1
        public string note;

        public Vector3 Dimensions =>
            dimensions_m != null && dimensions_m.Length == 3
                ? new Vector3(dimensions_m[0], dimensions_m[1], dimensions_m[2])
                : new Vector3(0.05f, 0.05f, 0.05f);

        public Color ColorHint =>
            color_hint != null && color_hint.Length >= 3
                ? new Color(color_hint[0], color_hint[1], color_hint[2])
                : new Color(0.82f, 0.82f, 0.80f);
    }

    [Serializable]
    public class PlacementSpec
    {
        public float[] local_position;        // centro del volumen visible, relativo a DeviceRoot
        public float[] local_rotation_euler;
        public PivotSpec pivot;

        public Vector3 LocalPosition =>
            local_position != null && local_position.Length == 3
                ? new Vector3(local_position[0], local_position[1], local_position[2])
                : Vector3.zero;

        public Quaternion LocalRotation =>
            local_rotation_euler != null && local_rotation_euler.Length == 3
                ? Quaternion.Euler(local_rotation_euler[0], local_rotation_euler[1], local_rotation_euler[2])
                : Quaternion.identity;
    }

    [Serializable]
    public class PivotSpec
    {
        public string mode;      // center | edge | explicit
        public string edge;      // front | back | left | right | top | bottom (mode=edge)
        public float[] offset_m; // (mode=explicit) offset del pivote respecto al centro
        public string note;
    }

    [Serializable]
    public class MovementSpec
    {
        public string type;      // hinge | slide_linear | rotate_axis | press | attach | static
        public string axis;      // right | up | forward (eje local)
        public RangeSpec range;
        public bool invert;      // invierte el signo del eje (ajuste de progreso, guía v2 §6.2)
        public Dictionary<string, string> progress_semantics;

        // Feedback visible al ejecutar la acción (solo para type = press): "blink"
        // engancha un pulso de EfsmHighlightPulse al cambio de estado de su máquina.
        // null / "none" → sin feedback. Efecto observable, no una máquina nueva.
        public string feedback;
    }

    /// <summary>Constantes de movement.type para evitar strings mágicos.</summary>
    public static class MovementTypeNames
    {
        public const string Hinge = "hinge";
        public const string SlideLinear = "slide_linear";
        public const string RotateAxis = "rotate_axis";
        public const string Press = "press";
        public const string Attach = "attach";   // soltar-y-pegar sin recorrido constreñido
        public const string Static = "static";
    }

    /// <summary>Constantes de movement.feedback para press.</summary>
    public static class FeedbackNames
    {
        public const string None = "none";
        public const string Blink = "blink";
    }

    [Serializable]
    public class AttachSpec
    {
        // Id (del device_spec) del componente al que este objeto se pega al
        // completar el gesto. GenericDeviceBuilder lo resuelve a un Transform.
        public string target_id;
        // Si true, al pegarse se coloca en la pose final capturada por el usuario
        // (o en el offset local por defecto); si false, se queda donde esté.
        public bool snap_to_end_pose = true;
        public string provenance;
    }

    [Serializable]
    public class RangeSpec
    {
        public float min;
        public float max;
        public string units;     // degrees | meters
    }

    [Serializable]
    public class XrRecipeSpec
    {
        public string transformer;        // p. ej. OneGrabRotateTransformer
        public ConstraintSpec constraints;
        public string movement_provider;  // p. ej. MoveFromTargetProvider
        public RigidbodySpec rigidbody;
        public string interaction;        // p. ej. EfsmPressTrigger (iconos planos)
    }

    [Serializable]
    public class ConstraintSpec
    {
        public float min;
        public float max;
    }

    [Serializable]
    public class RigidbodySpec
    {
        public bool is_kinematic = true;
        public bool use_gravity = false;
    }

    [Serializable]
    public class EfsmBindingSpec
    {
        public string dimension;          // dimensión del EFSM que gobierna este componente
        public float open_threshold = 0.85f;
        public float closed_threshold = 0.15f;

        // Acciones de la máquina EFSM invocadas al soltar el agarre por encima
        // del umbral superior / por debajo del inferior (ConstrainedMover
        // actionAtMax / actionAtMin). Deben coincidir con acciones existentes
        // en la máquina machine_id del EFSM JSON — lo comprueba el validador V3.
        public string action_at_max;
        public string action_at_min;

        // Acción invocada por EfsmPressTrigger en componentes movement=press
        // (botones, superficies que se tocan). Misma exigencia de coincidencia
        // con la máquina del EFSM.
        public string press_action;
    }
}
