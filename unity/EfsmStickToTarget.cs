// ============================================================================
// EfsmStickToTarget.cs — Pegado de un componente a otro (Fase 5/6)
// Proyecto: simulaciones MR desde manuales · Núcleo EFSM
// Ubicación sugerida: Assets/EFSM/Core/EfsmStickToTarget.cs
//
// QUÉ HACE
//   Se añade a un componente móvil (p. ej. la cápsula o la taza) y, cuando el
//   gesto se completa, lo REPARENTA bajo el componente 'target' y lo deja fijo
//   ahí (deja de poder agarrarse/moverse). Modela el 'attach_to' del pipeline:
//     · nespresso_capsule -> capsule_container
//     · cup               -> drip_grid
//
//   Uso mínimo (lo que pediste): añade el componente, arrastra al campo 'target'
//   el GameObject del componente al que debe pegarse, y listo.
//
// DISPARO (campo 'trigger'):
//   · OnMoverComplete (por defecto): se pega cuando el ConstrainedMover del
//     propio objeto supera 'completeThreshold' (extremo abierto del recorrido).
//     Es el modo natural: la cápsula se pega al insertarse, la taza al colocarse.
//   · OnStart: se pega nada más arrancar (útil para escenas de estado final).
//   · Manual: solo se pega al llamar Attach() (o el menú contextual del Inspector),
//     por ejemplo desde el ITS.
//
// NOTAS DE DISEÑO (honestidad — no verificado en un Unity real)
//   · No referencia ningún tipo del SDK de Meta. Para "congelar" el objeto
//     desactiva el ConstrainedMover y el OVRGrabbableAdapter (tipos del núcleo)
//     y pone el Rigidbody en kinematic. El Grabbable/HandGrabInteractable del
//     SDK, si existe, conviene desactivarlo también: se expone 'behavioursToDisable'
//     para arrastrarlo desde el Inspector sin crear dependencia de compilación.
//   · Reparenta bajo el TARGET tal cual se arrastre. Por convención del proyecto
//     ese transform debe ir sin escala no uniforme (raíz del componente), para
//     no deformar la geometría hija.
//   · Detach() restaura el padre original: pensado para el reset del procedimiento.
// ============================================================================

using UnityEngine;

namespace EFSM
{
    public class EfsmStickToTarget : MonoBehaviour
    {
        public enum Trigger { OnMoverComplete, OnStart, Manual }

        [Header("Objetivo")]
        [Tooltip("Componente al que pegarse. Arrastra aquí su GameObject.")]
        public Transform target;

        [Header("Disparo")]
        public Trigger trigger = Trigger.OnMoverComplete;
        [Tooltip("Progreso 0..1 del ConstrainedMover que dispara el pegado (modo OnMoverComplete).")]
        [Range(0f, 1f)] public float completeThreshold = 0.85f;

        [Header("Pose al pegarse")]
        [Tooltip("Si está activo, coloca el objeto en una pose local relativa al target. " +
                 "Si está desactivado, se queda pegado donde esté en ese momento (conserva pose mundial).")]
        public bool snapToTargetPose = true;
        public Vector3 localPositionOffset = Vector3.zero;
        public Vector3 localEulerOffset = Vector3.zero;

        [Header("Congelado tras pegarse")]
        [Tooltip("Desactiva el agarre/movimiento para que no se despegue.")]
        public bool freezeAfterAttach = true;
        [Tooltip("Behaviours extra a desactivar al pegarse (p. ej. Grabbable / HandGrabInteractable del SDK). " +
                 "Se arrastran desde el Inspector para no crear dependencia de compilación con el SDK.")]
        public Behaviour[] behavioursToDisable;

        // --- runtime -------------------------------------------------------
        private ConstrainedMover _mover;
        private Transform _originalParent;
        private Vector3 _originalLocalPos;
        private Quaternion _originalLocalRot;
        private bool _attached;

        public bool IsAttached => _attached;

        private void Awake()
        {
            _mover = GetComponent<ConstrainedMover>();
            _originalParent   = transform.parent;
            _originalLocalPos = transform.localPosition;
            _originalLocalRot = transform.localRotation;
        }

        private void Start()
        {
            if (trigger == Trigger.OnStart) Attach();
        }

        private void Update()
        {
            if (_attached || trigger != Trigger.OnMoverComplete || _mover == null) return;
            if (_mover.GetNormalizedProgress() >= completeThreshold) Attach();
        }

        // -------------------------------------------------------------------
        [ContextMenu("Attach")]
        public void Attach()
        {
            if (_attached) return;
            if (target == null)
            {
                Debug.LogWarning($"[EfsmStickToTarget] '{name}': falta 'target'. No se pega.");
                return;
            }

            transform.SetParent(target, worldPositionStays: !snapToTargetPose);

            if (snapToTargetPose)
            {
                transform.localPosition = localPositionOffset;
                transform.localRotation = Quaternion.Euler(localEulerOffset);
            }

            if (freezeAfterAttach) Freeze();

            _attached = true;
            Debug.Log($"[EfsmStickToTarget] '{name}' pegado a '{target.name}'.");
        }

        [ContextMenu("Detach (reset)")]
        public void Detach()
        {
            if (!_attached) return;

            transform.SetParent(_originalParent, worldPositionStays: false);
            transform.localPosition = _originalLocalPos;
            transform.localRotation = _originalLocalRot;

            Unfreeze();
            _attached = false;
            Debug.Log($"[EfsmStickToTarget] '{name}' despegado (reset).");
        }

        // -------------------------------------------------------------------
        private void Freeze()
        {
            if (_mover != null) _mover.enabled = false;

            var grab = GetComponent<OVRGrabbableAdapter>();
            if (grab != null) grab.enabled = false;

            var rb = GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;

            if (behavioursToDisable != null)
                foreach (var b in behavioursToDisable)
                    if (b != null) b.enabled = false;
        }

        private void Unfreeze()
        {
            if (_mover != null) _mover.enabled = true;

            var grab = GetComponent<OVRGrabbableAdapter>();
            if (grab != null) grab.enabled = true;

            if (behavioursToDisable != null)
                foreach (var b in behavioursToDisable)
                    if (b != null) b.enabled = true;
        }
    }
}
