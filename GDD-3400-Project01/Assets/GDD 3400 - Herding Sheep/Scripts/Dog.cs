using System.Collections.Generic;
using UnityEngine;

namespace GDD3400.Project01
{
    public class Dog : MonoBehaviour
    {
        #region prebuilt code
        // Active State - Set to false to deactivate the dog
        private bool _isActive = true;

        // Property to get/set active state of the dog
        public bool IsActive
        {
            get => _isActive;
            set => _isActive = value;
        }

        // Required Variables (Do not edit!)
        private float _maxSpeed = 5f;
        private float _sightRadius = 7.5f;

        // Layers - Set In Project Settings
        public LayerMask _targetsLayer;
        public LayerMask _obstaclesLayer;

        // Tags - Set In Project Settings
        private string friendTag = "Friend";
        private string threatTag = "Threat";
        private string safeZoneTag = "SafeZone";
        #endregion

        // Memory - Store information about perceived objects
        private Vector3 safeZonePos;
        private Dictionary<GameObject, Vector3> locatedFriends = new Dictionary<GameObject, Vector3>();

        // Movement Variables
        private float accelerationForce = 20f; // force applied for acceleration
        private float maxTurnSpeed = 60f; // degrees per second
        private float wanderAngleOffset = 0f; // current wander angle offset in degrees
        private float wanderMaxOffset = 30f; // max angle offset for wandering in degrees

        // Steering Tunables
        [SerializeField] private float avoidDistance = 4.0f;      // how far ahead to check for obstacles
        [SerializeField] private float avoidRadius = 0.4f;      // spherecast radius
        [SerializeField] private float corralBehindDistance = 3f; // how far behind the sheep to stay
        [SerializeField] private float slowDownRadius = 4f; // within this distance slow down
        [SerializeField] private float minThrottle = 0.10f; // never drop below this

        // Counters and Timers
        private static float wanderInterval = 0f; // timer for seconds between random wander direction changes
        private const float maxWanderInterval = 2f; // max seconds between random wander direction changes

        // References
        private Rigidbody rb; // reference to rigid body
        private float originalOrientation; // in degrees for yaw only


        public void Awake()
        {
            // Find the layers in the project settings
            _targetsLayer = LayerMask.GetMask("Targets");
            _obstaclesLayer = LayerMask.GetMask("Obstacles");

            // Assign safezone position
            GameObject safeZoneObj = GameObject.FindWithTag(safeZoneTag);
            if (safeZoneObj != null)
            {
                safeZonePos = safeZoneObj.transform.position;
                Debug.Log("SafeZone position assigned: " + safeZonePos);
            }
            else
            {
                Debug.LogWarning("SafeZone object not found in the scene.");
            }

            // Assign threat tag if it isn't already
            this.tag = threatTag;

            // Get and configure rigidbody component for physics based movement 
            rb = GetComponent<Rigidbody>();
            rb.isKinematic = false; // ensure physics affects the dog
            rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ; // prevent tipping over

            originalOrientation = transform.rotation.eulerAngles.y; // store original yaw orientation
        }


        /// <summary>
        /// Make sure to use FixedUpdate for movement with physics based Rigidbody
        /// </summary>
        private void FixedUpdate()
        {
            if (!_isActive) return;

            Perception();  // detect friends and update memory
            DecisionMaking(); // choose actions based on memory and safe zone
        }

        /// <summary>
        /// Perception phase to detect friends within sight radius and update memory
        /// </summary>
        private void Perception()
        {
            // Use overlapsphere to find friends and assign to friendList
            var friendList = FindFriend();
            // Update located friends dictionary
            if (friendList.Count > 0)
            {
                // Add new friends to the dictionary with their positions
                foreach (var friend in friendList)
                {
                    // Only add if not already known
                    if (!locatedFriends.ContainsKey(friend))
                    {
                        // Add new friend with current position
                        locatedFriends.Add(friend, friend.transform.position);
                        Debug.Log("New friend located: " + friend.name + " at position " + friend.transform.position);
                    }
                    else
                    {
                        // Update position if already known
                        locatedFriends[friend] = friend.transform.position;
                    }
                }
            }
            else
            {
                // Commented out to reduce log spam, use only for debugging
                // Debug.Log("No friends detected within sight radius.");
            }
        }

        /// <summary>
        /// Decision making phase to choose actions based on perceived friends and safe zone
        /// </summary>
        private void DecisionMaking()
        {
            // (1) If no friends are located, wander instead
            if (locatedFriends.Count == 0)
            {
                Wander(); // call wander behavior
                return;
            }

            // (2) Setup tracking for furthest friend (sheep) from safe zone
            GameObject targetSheep = null; // the sheep to corral
            float maxDistSq = float.MinValue; // max squared distance to safe zone found so far

            // (3) Cleanup: remove null/destroyed friends, find furthest sheep
            var toRemove = new List<GameObject>(); // temporary list for cleanup
            foreach (var keyVal in locatedFriends) // key = friend GameObject, value = last known position
            {
                if (keyVal.Key == null)
                {
                    toRemove.Add(keyVal.Key); // mark for removal
                    continue; // skip destroyed
                }
                float d2 = (keyVal.Value - safeZonePos).sqrMagnitude; // sheep+safeZone squared distance

                // Find the friend furthest from the safe zone to corral first
                if (d2 > maxDistSq)
                {
                    maxDistSq = d2; // update max distance
                    targetSheep = keyVal.Key; // set as target sheep
                }
            }

            // (4) Remove null/destroyed entries from memory
            foreach (var dead in toRemove)
            {
                locatedFriends.Remove(dead);
            }

            // (5) If no valid sheep found, wander again
            if (targetSheep == null)
            {
                Wander();
                return;
            }

            // (6) Compute sub-goal position behind chosen sheep (relative to safe zone)
            Vector3 sheepPos = targetSheep.transform.position;
            Vector3 subGoal = CalculateDogSubGoal(sheepPos, safeZonePos, corralBehindDistance);

            // (7) Steer toward that sub-goal (handles obstacle avoidance + physics)
            Steering(subGoal);
        }

        /// <summary>
        /// Steers toward a target while avoiding obstacles
        /// Updates originalOrientation (yaw) here only so wander can use it
        /// Applies rotation capped by maxTurnSpeed
        /// Applies forward acceleration capped by _maxSpeed
        /// </summary>
        /// <param name="targetPos"></param>
        private void Steering(Vector3 targetPosition)
        {
            if (!_isActive) return;

            // (1) Compute desired vector toward target
            Vector3 toTarget = targetPosition - transform.position;
            toTarget.y = 0f; // keep movement in the horizontal plane

            // (2) Measure distance to target
            float distance = toTarget.magnitude;

            // (3) Handle "already there" case
            if (distance < 0.1f) // basically here
            {
                toTarget = transform.forward;   // keep heading if target is basically here
                distance = 0f;
            }
            else // not at target yet
            {
                toTarget /= distance;           // normalize
            }

            #region Wall Avoidance
            // (4) Get forward vector
            Vector3 forward = transform.forward; // current forward direction
            forward.y = 0f; // keep in horizontal plane
            forward.Normalize(); // should be normalized already, but just in case

            // (5) Spherecast ahead to detect obstacles
            RaycastHit hit; // for spherecast info
            // Spherecast ahead to detect obstacles within avoidDistance and avoidRadius (thickness) on obstacles layer only - ignore triggers 
            bool blocked = Physics.SphereCast(
                transform.position + Vector3.up * 0.1f, // start a bit above ground to avoid floor
                avoidRadius, // radius of the sphere to cast
                forward, // direction to cast the sphere
                out hit, // output hit info if blocked ahead
                avoidDistance, // how far ahead to check for obstacles
                _obstaclesLayer
            );

            // (6) Adjust steering if blocked
            // if blocked, compute a reflection vector and blend it with toTarget
            if (blocked)
            {
                Vector3 away = Vector3.Reflect(forward, hit.normal); // reflect forward on hit normal
                away.y = 0f; // keep in horizontal plane
                if (away.sqrMagnitude > 0.001f) away.Normalize(); // normalize if not bad case

                // Blend mostly away so we slide off the surface
                toTarget = Vector3.Slerp(toTarget, away, 0.8f); // slerp for smoothness (80% away, 20% toTarget)
                if (toTarget.sqrMagnitude < 0.001f) toTarget = away; // bad case safety
            }
            #endregion

            // (7) Compute target yaw from toTarget vector
            // Compute target yaw and update originalOrientation so wander can use it
            // Atan2 returns radians; convert to degrees and adjust for Unity's coordinate system
            float targetYaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            // Pass the target yaw to originalOrientation for wander to use as the base orientation
            originalOrientation = targetYaw;

            // (8) Smooth rotation toward target yaw
            // Smoothly rotate toward target yaw (deg/step capped)
            float currentYaw = transform.rotation.eulerAngles.y; // current yaw in degrees
            float delta = Mathf.DeltaAngle(currentYaw, targetYaw); // shortest angle difference (-180..180)
            float maxStep = maxTurnSpeed * Time.fixedDeltaTime; // max turn this frame
            float step = Mathf.Clamp(delta, -maxStep, maxStep); // clamp to max turn speed
            rb.MoveRotation(Quaternion.Euler(0f, currentYaw + step, 0f)); // apply rotation

            // (9) Arrival throttle based on distance
            float t = (slowDownRadius <= 0f) ? 1f : Mathf.Clamp01(distance / slowDownRadius);
            float throttle = Mathf.SmoothStep(minThrottle, 1f, t);

            // (10) Apply slowdown if obstacle detected
            if (blocked)
            {
                // Scale throttle down a bit when something's ahead; tweakable
                throttle *= 0.15f;
            }

            // (11) Add forward force with throttle
            rb.AddForce(transform.forward * (accelerationForce * throttle), ForceMode.Acceleration);

            // (12) Clamp linear speed
            if (rb.linearVelocity.magnitude > _maxSpeed)
            {
                rb.linearVelocity = rb.linearVelocity.normalized * _maxSpeed;
            }

            // (13) Check angular velocity (leave linear velocity alone)
            if (!float.IsFinite(rb.angularVelocity.x) || !float.IsFinite(rb.angularVelocity.y) || !float.IsFinite(rb.angularVelocity.z))
                rb.angularVelocity = Vector3.zero;
        }

        /// <summary>
        /// Wander behavior to move randomly within an area by adjusting the yaw angle over time with smooth turning and forward acceleration
        /// toward a target direction
        /// </summary>
        private void Wander()
        {
            // Refresh wander offset at intervals (don’t thrash every frame)
            wanderInterval += Time.fixedDeltaTime;
            if (wanderInterval >= maxWanderInterval)
            {
                wanderAngleOffset = Random.Range(-wanderMaxOffset, wanderMaxOffset);
                wanderInterval = 0f;
            }

            // Target yaw = base orientation + offset
            float wanderYaw = originalOrientation + wanderAngleOffset;

            // Build a point a few meters ahead in that yaw, then let Steering handle the rest
            Quaternion yawRot = Quaternion.Euler(0f, wanderYaw, 0f);
            Vector3 ahead = transform.position + yawRot * Vector3.forward * Mathf.Max(avoidDistance * 1.5f, 3f);

            Steering(ahead);
        }


        /// <summary>
        /// Find all friends within sight radius using OverlapSphere and filter by "Friend" tag
        /// </summary>
        /// <returns></returns>
        private List<GameObject> FindFriend()
        {
            // Find all colliders within sight radius on the targets layer
            Collider[] hitColliders = Physics.OverlapSphere(transform.position, _sightRadius, _targetsLayer);

            // List to store found friends
            var friends = new List<GameObject>();
            // Filter for "Friend" tag
            foreach (var collider in hitColliders)
            {
                if (collider.CompareTag(friendTag))
                {
                    friends.Add(collider.gameObject);
                }
            }
            return friends;
        }


        /// <summary>
        /// Calculate a sub-goal position for the dog to move to in order to corral the sheep towards the safe zone
        /// </summary>
        /// <param name="sheepPos"></param>
        /// <param name="safeZonePos"></param>
        /// <param name="distanceBehindSheep"></param>
        /// <returns></returns>
        private Vector3 CalculateDogSubGoal(Vector3 sheepPos, Vector3 safeZonePos, float distanceBehindSheep)
        {
            #region Developer Notes
            // I need math to calculate steering behaviors that position the dog behind the sheep to corral them towards the safe zone
            // I also need to manage the dog's memory of located friends and update their positions as they move
            // The dog should be able to handle multiple friends at once and prioritize those closest to the safe zone
            // The dog should also have a slight wander behavior when no friends are located to make it seem more natural

            // Assign a sub-goal target destination for the dog based on a fixed position relative to the sheep being corralled and the safe zone
            // If the safe zone is west of the sheep, a position south east of the sheep would be ideal for the dog to move to in order to push the
            // sheep towards the safe zone until it is directly between the dog and the safe zone, in which case the dog should move to a position
            // directly behind the sheep relative to the safe zone

            // If an additional sheep is located while corralling, update the dog's memory with the new sheep's position and set a sub-goal to corral
            // that sheep first before returning to the original sheep
            // If there are multiple sheep being corralled, prioritize on pushing the one furthest from the safe zone first

            // The invisible sub-goal target position for the dog should be recalculated every frame based on the current position of the sheep being corralled
            // and the safe zone, so that the dog can adjust its position dynamically as the sheep moves

            // To find this invisible sub-goal target destination vector for the dog, I can take the vector from the sheep to the safe zone,
            // normalize it, add a perpendicular vector to it, and scale it by a fixed distance to position the dog behind the sheep relative
            // to the safe zone no matter where the safe zone is located
            #endregion

            #region Original Attempt archived for Reference
            //// Calculate the vector from the sheep to the safe zone
            //Vector3 toSafeZone = (safeZonePos - sheepPos);
            //toSafeZone.y = 0f; // Keep movement in the horizontal plane

            //// Normalize the vector to get the direction
            //if (toSafeZone.sqrMagnitude < 0.0001f)
            //    toSafeZone = Vector3.forward; // Default direction if sheep is at safe zone
            //toSafeZone.Normalize();

            //// Calculate right and left vectors relative to the toSafeZone direction
            //Vector3 right = Vector3.Cross(Vector3.up, toSafeZone).normalized;
            //Vector3 left = -right;

            //// Calculate the base position behind the sheep
            //Vector3 baseBehind = sheepPos - toSafeZone * distanceBehindSheep;

            //// Choose a side (right or left) to position the dog slightly off-center behind the sheep
            //Vector3 rightGoal = baseBehind + right * (distanceBehindSheep / 2f);
            //Vector3 leftGoal = baseBehind + left * (distanceBehindSheep / 2f);

            //// Choose whichever goal is closer to the dog's current pos
            //float distRight = (rightGoal - transform.position).sqrMagnitude;
            //float distLeft = (leftGoal - transform.position).sqrMagnitude;

            //// Return the chosen sub-goal position for the dog
            //Vector3 dogSubGoal = (distRight <= distLeft ? rightGoal : leftGoal);
            //return dogSubGoal;
            #endregion

            // (1) Compute vector from sheep to safe zone (herding direction)
            Vector3 toSafe = safeZonePos - sheepPos;
            toSafe.y = 0f; // keep in horizontal plane

            // (2) Handle "bad case" if sheep is basically at safe zone
            if (toSafe.sqrMagnitude < 0.001f)
                toSafe = Vector3.forward; // bad case safety
            toSafe.Normalize(); // normalize to get direction

            // (3) Compare squared distances of sheep and dog to safe zone
            float dSheep = (sheepPos - safeZonePos).sqrMagnitude;
            float dDog = (transform.position - safeZonePos).sqrMagnitude;

            // (4) If dog is OUTSIDE (further than sheep), go directly behind sheep
            if (dDog > dSheep) // fixed
            {
                Vector3 behind = sheepPos - toSafe * distanceBehindSheep;
                return behind;
            }

            // (5) Otherwise, dog is INSIDE (closer than sheep), use off-center positioning
            //     Build perpendicular directions to toSafe
            // Cross product gives a right vector perpendicular to toSafe and up (y axis)
            // so that we can add the other vector to it to get an off-center behind position
            Vector3 right = Vector3.Cross(Vector3.up, toSafe).normalized; 
            Vector3 left = -right;

            // (6) Base "behind sheep" position
            Vector3 baseBehind = sheepPos - toSafe * distanceBehindSheep;
            // (7) Offset right and left goals for off-center sub-goals
            Vector3 rightGoal = baseBehind + right * (distanceBehindSheep * 0.5f);
            Vector3 leftGoal = baseBehind + left * (distanceBehindSheep * 0.5f);

            // (8) Pick whichever side is closer to the dog
            float distRight = (rightGoal - transform.position).sqrMagnitude;
            float distLeft = (leftGoal - transform.position).sqrMagnitude;

            // (9) Return chosen sub-goal
            return (distRight <= distLeft ? rightGoal : leftGoal);
        }
    }
}


#region Original Code Archive
//using UnityEngine;

//namespace GDD3400.Project01
//{
//    public class Dog : MonoBehaviour
//    {

//        private bool _isActive = true;
//        public bool IsActive
//        {
//            get => _isActive;
//            set => _isActive = value;
//        }

//        // Required Variables (Do not edit!)
//        private float _maxSpeed = 5f;
//        private float _sightRadius = 7.5f;

//        // Layers - Set In Project Settings
//        public LayerMask _targetsLayer;
//        public LayerMask _obstaclesLayer;

//        // Tags - Set In Project Settings
//        private string friendTag = "Friend";
//        private string threatTag = "Thread";
//        private string safeZoneTag = "SafeZone";



//        public void Awake()
//        {
//            // Find the layers in the project settings
//            _targetsLayer = LayerMask.GetMask("Targets");
//            _obstaclesLayer = LayerMask.GetMask("Obstacles");

//        }

//        private void Update()
//        {
//            if (!_isActive) return;

//            Perception();
//            DecisionMaking();
//        }

//        private void Perception()
//        {

//        }

//        private void DecisionMaking()
//        {

//        }

//        /// <summary>
//        /// Make sure to use FixedUpdate for movement with physics based Rigidbody
//        /// You can optionally use FixedDeltaTime for movement calculations, but it is not required since fixedupdate is called at a fixed rate
//        /// </summary>
//        private void FixedUpdate()
//        {
//            if (!_isActive) return;

//        }
//    }
//}
#endregion