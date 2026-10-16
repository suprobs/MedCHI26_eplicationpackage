using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;

namespace EFSM
{
    /// <summary>
    /// Traduce los eventos de agarre del Meta XR SDK a la semantica EFSM
    /// (ConstrainedMover + acciones lift/close). Desde la base Editor-first,
    /// este adapter YA NO crea ni inyecta componentes XR: la receta (Rigidbody
    /// kinematico + Grabbable + HandGrabInteractable + transformer) se configura
    /// en el Editor y queda serializada en la escena. Aqui solo se VALIDA que la
    /// receta este completa — con un error legible si falta algo — y se hace de
    /// puente de eventos. GrabInteractable (mando) es opcional: la escena base
    /// actual usa solo manos (bloque Real Hands).
    /// </summary>
    [RequireComponent(typeof(ConstrainedMover))]
    [RequireComponent(typeof(EfsmComponentBinder))]
    public class OVRGrabbableAdapter : MonoBehaviour
    {
        public Color grabColor = Color.yellow;

        private HandGrabInteractable _handGrabInteractable; // requerido (manos)
        private GrabInteractable _grabInteractable;         // opcional (mando)
        private ConstrainedMover _mover;
        private EfsmComponentBinder _binder;
        private Renderer _renderer;
        private Color _originalColor;

        // Estado real de agarre: el fin de agarre solo se procesa si hubo un
        // inicio previo. Evita los "RELEASED. Progress: 0%" fantasma que el ciclo
        // Normal<->Hover disparaba (y que contaminaban las metricas del ITS).
        private bool _isGrabbed;

        private void Awake()
        {
            _mover = GetComponent<ConstrainedMover>();
            _binder = GetComponent<EfsmComponentBinder>();
            _renderer = GetComponentInChildren<Renderer>();
            if (_renderer != null) _originalColor = _renderer.material.color;

            if (!ValidateRecipe()) enabled = false;
        }

        /// <summary>
        /// Comprueba la receta de agarrable configurada en Editor. Devuelve false
        /// (y explica exactamente que falta) en lugar de intentar repararla en
        /// runtime: la reparacion automatica via AddComponent/inyeccion fue la
        /// fuente de los AssertionException del SDK y se ha abandonado.
        /// </summary>
        private bool ValidateRecipe()
        {
            bool ok = true;

            var rb = GetComponent<Rigidbody>();
            if (rb == null)
            {
                Debug.LogError($"[OVRGrabbableAdapter] '{name}': falta Rigidbody. Anadirlo en el Editor con isKinematic=true.", this);
                ok = false;
            }
            else if (!rb.isKinematic)
            {
                // No es fatal, pero es la configuracion que demostro pelear con el
                // Grabbable. Se avisa y se corrige en Editor, no aqui.
                Debug.LogWarning($"[OVRGrabbableAdapter] '{name}': Rigidbody con isKinematic=false. La receta validada usa isKinematic=true; revisar en el Editor.", this);
            }

            if (GetComponent<Grabbable>() == null)
            {
                Debug.LogError($"[OVRGrabbableAdapter] '{name}': falta Grabbable. Anadirlo en el Editor y arrastrar el transformer a su campo 'One Grab Transformer'.", this);
                ok = false;
            }

            _handGrabInteractable = GetComponent<HandGrabInteractable>();
            if (_handGrabInteractable == null)
            {
                Debug.LogError($"[OVRGrabbableAdapter] '{name}': falta HandGrabInteractable (agarre con manos). Anadirlo en el Editor con su campo Rigidbody asignado.", this);
                ok = false;
            }

            _grabInteractable = GetComponent<GrabInteractable>(); // opcional, sin error si falta

            if (GetComponent<Collider>() == null)
            {
                Debug.LogError($"[OVRGrabbableAdapter] '{name}': falta un Collider para que el interactor detecte el objeto.", this);
                ok = false;
            }

            return ok;
        }

        private void OnEnable()
        {
            if (_handGrabInteractable != null) _handGrabInteractable.WhenStateChanged += OnStateChanged;
            // Mismo manejador para ambos: a ConstrainedMover no le importa que
            // interactor concreto disparo el agarre, solo si el objeto esta agarrado.
            if (_grabInteractable != null) _grabInteractable.WhenStateChanged += OnStateChanged;
        }

        private void OnDisable()
        {
            if (_handGrabInteractable != null) _handGrabInteractable.WhenStateChanged -= OnStateChanged;
            if (_grabInteractable != null) _grabInteractable.WhenStateChanged -= OnStateChanged;
        }

        private void OnStateChanged(InteractableStateChangeArgs args)
        {
            if (args.NewState == InteractableState.Select)
            {
                if (!_isGrabbed)
                {
                    _isGrabbed = true;
                    HandleGrabStart();
                }
            }
            else if (_isGrabbed && args.PreviousState == InteractableState.Select)
            {
                // Solo es liberacion real si venimos de Select: las transiciones
                // Normal<->Hover del ciclo de hover no cuentan como release.
                _isGrabbed = false;
                HandleGrabEnd();
            }
        }

        private void HandleGrabStart()
        {
            _mover.OnGrabStart();
            if (_renderer != null) _renderer.material.color = grabColor;
            Debug.Log($"[OVRGrabbableAdapter] '{_binder.componentId}' GRABBED.");
        }

        private void HandleGrabEnd()
        {
            _mover.OnGrabEnd();
            if (_renderer != null) _renderer.material.color = _originalColor;

            var progress = _mover.GetNormalizedProgress();
            Debug.Log($"[OVRGrabbableAdapter] '{_binder.componentId}' RELEASED. Progress: {progress * 100f:F0}%");

            string action = null;
            if (progress >= _mover.openThreshold) action = _mover.actionAtMax;
            else if (progress <= _mover.closedThreshold) action = _mover.actionAtMin;

            if (!string.IsNullOrEmpty(action))
                EfsmStateManager.Instance?.TriggerAction(_binder.machineId, action);

            // Confirmacion haptica. PENDIENTE (cambio aparte): solo tiene sentido
            // con agarre de mando; con pinch de mano no hay actuador, y RTouch
            // presupone mano dominante derecha. No se toca en esta iteracion.
            OVRInput.SetControllerVibration(0.2f, 0.4f, OVRInput.Controller.RTouch);
        }
    }
}