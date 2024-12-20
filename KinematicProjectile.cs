using UnityEngine;
using Fusion;
using System.Collections.Generic;

namespace TPSBR
{
	public class KinematicProjectile : Projectile
	{
		// PUBLIC MEMBERS

		public float FireDespawnTime => _fireDespawnTime;

		// PROTECTED MEMBERS

		[Networked]
		protected ProjectileData _data { get; private set; }
		[Networked][OnChangedRender(nameof(OnBounceCountChanged))]
		protected int            _bounceCount { get; private set; }

		// PRIVATE MEMBERS

		[SerializeField]
		private ProjectileDamage _damage;
		[SerializeField]
		private float            _fireDespawnTime = 3f;
		[SerializeField]
		private float            _impactDespawnTime = 1f;
		[SerializeField]
		private float            _gravity = 20f;
		[SerializeField]
		private GameObject       _impactEffect;
		[SerializeField]
		private ImpactSetup      _impactSetup;
		[SerializeField]
		private float            _impactPenetration = 0f;
		[SerializeField]
		private bool             _spawnImpactEffectOnTimeout = false;
		[SerializeField]
		private bool             _spawnImpactOnStaticHitOnly = true;

		[SerializeField]
		private float            _showProjectileVisualAfterDistance = 0f;
		[SerializeField]
		private GameObject       _projectileVisual;
		[SerializeField]
		private Transform        _dummyRotationTarget;
		[SerializeField]
		private float            _velocityToRotationMultiplier = 10f;

		[Header("Bounce")]
		[SerializeField]
		private bool             _canBounce = false;
		[SerializeField]
		private float            _bounceVelocityMultiplier = 0.7f;
		[SerializeField]
		private float            _bounceObjectRadius = 0.05f;
		[SerializeField]
		private AudioEffect      _bounceEffect;

		private float            _maxBounceVolume;

		private EHitType         _hitType;
		private LayerMask        _hitMask;
		private int              _ownerObjectInstanceID;
		private List<LagCompensatedHit> _validHits = new(16);

		private TrailRenderer    _trailRenderer;
		private bool             _hasImpactedVisual;

		// PUBLIC METHODS

		public override void Fire(NetworkObject owner, Vector3 firePosition, Vector3 initialVelocity, LayerMask hitMask, EHitType hitType)
		{
			ProjectileData data = default;

			data.FirePosition = firePosition;
			data.InitialVelocity = initialVelocity;
			data.DespawnCooldown = TickTimer.CreateFromSeconds(Runner, _fireDespawnTime);
			data.StartTick = Runner.Tick;

			_hitMask = hitMask;
			_hitType = hitType;

			_ownerObjectInstanceID = owner != null ? owner.gameObject.GetInstanceID() : 0;

			_data = data;
			_bounceCount = default;
		}

		public void SetDespawnCooldown(float cooldown)
		{
			var data = _data;
			data.DespawnCooldown = TickTimer.CreateFromSeconds(Runner, cooldown);
			_data = data;
		}

		// NetworkBehaviour INTERFACE

		public override void Spawned()
		{
			_projectileVisual.SetActiveSafe(_showProjectileVisualAfterDistance <= 0f);

			_hasImpactedVisual = false;

			if (_trailRenderer != null)
			{
				_trailRenderer.Clear();
			}
		}

		public override void Despawned(NetworkRunner runner, bool hasState)
		{
			if (_dummyRotationTarget != null)
			{
				_dummyRotationTarget.rotation = Quaternion.identity;
			}
		}

		public override void FixedUpdateNetwork()
		{
			var data = _data;

			if (CalculateProjectile(ref data) == true)
			{
				_data = data;
			}
		}

		public override void Render()
		{
			RenderProjectile(_data);
		}

		// MONOBEHAVIOUR

		protected void Awake()
		{
			_trailRenderer = GetComponentInChildren<TrailRenderer>();
			_maxBounceVolume = _bounceEffect != null ? _bounceEffect.DefaultSetup.Volume : 0f;
		}

		// PROTECTED METHODS

		protected void OnBounceCountChanged()
		{
			if (_bounceEffect == null)
				return;

			var soundSetup = _bounceEffect.DefaultSetup;
			soundSetup.Volume = Mathf.Lerp(0f, _maxBounceVolume, _data.InitialVelocity.magnitude / 10f);

			_bounceEffect.Play(soundSetup, EForceBehaviour.ForceAny);
		}

		// PRIVATE METHODS

		private void RenderProjectile(ProjectileData data)
		{
			// Spawn impact if not shown yet
			if (data.HasImpacted == true && _hasImpactedVisual == false)
			{
				SpawnImpact(ref data, data.ImpactPosition, data.ImpactNormal, data.ImpactTagHash);
				_projectileVisual.SetActiveSafe(false);
			}

			if (_trailRenderer != null)
			{
				_trailRenderer.emitting = data.IsFinished == false;
			}

			if (data.IsFinished == true)
			{
				transform.position = data.FinishedPosition;
				return;
			}

			if (TryGetSnapshotsBuffers(out NetworkBehaviourBuffer from, out NetworkBehaviourBuffer to, out float alpha) == false)
				return;

			float renderTime = Object.IsProxy == true ? Runner.RemoteRenderTime : Runner.LocalRenderTime;
			float floatTick = renderTime / Runner.DeltaTime;

			transform.position = GetProjectilePosition(ref data, floatTick);

			var direction = floatTick - 1f < data.StartTick ? data.InitialVelocity : transform.position - GetProjectilePosition(ref data, floatTick - 1f);
			transform.rotation = Quaternion.LookRotation(direction.normalized);

			if (_showProjectileVisualAfterDistance > 0f && Vector3.Distance(data.FirePosition, transform.position) > _showProjectileVisualAfterDistance)
			{
				// Delaying showing projectile visual is dummy approach for solving differences between
				// weapon barrel position and actual fire position (near character shoulder).
				// Check more elaborate approach to projectiles in Fusion Projectiles project.
				_projectileVisual.SetActiveSafe(true);
			}

			if (_dummyRotationTarget != null)
			{
				var axis = Vector3.Cross(Vector3.up, data.InitialVelocity);
				_dummyRotationTarget.Rotate(axis, Time.deltaTime * data.InitialVelocity.magnitude * _velocityToRotationMultiplier, Space.World);
			}
		}

		private bool CalculateProjectile(ref ProjectileData data)
		{
			if (data.DespawnCooldown.Expired(Runner) == true)
			{
				if (data.HasStopped == false)
				{
					data.FinishedPosition = GetProjectilePosition(ref data, Runner.Tick);
					data.HasStopped = true;
				}

				if (_spawnImpactEffectOnTimeout == true && data.HasImpacted == false)
				{
					SpawnImpact(ref data, data.FinishedPosition, Vector3.up, 0);
				}

				Runner.Despawn(Object);
				return false;
			}

			if (data.IsFinished == true)
				return true;

			var newPosition = GetProjectilePosition(ref data, Runner.Tick);
			var previousPosition = GetProjectilePosition(ref data, Runner.Tick - 1);

			var direction = newPosition - previousPosition;
			float distance = direction.magnitude;

			if (distance <= 0f)
				return true;

			direction /= distance; // Normalize

			if (ProjectileUtility.ProjectileCast(Runner, Object.InputAuthority, _ownerObjectInstanceID, previousPosition - direction * _bounceObjectRadius, direction, distance + 2 * _bounceObjectRadius, _hitMask, _validHits) == true)
			{
				if (_canBounce == true)
				{
					ProcessBounce(ref data, _validHits[0], direction, distance + _bounceObjectRadius * 2f);
				}
				else
				{
					ProcessHit(ref data, _validHits[0], direction);
				}
			}

			return true;
		}

		private void ProcessHit(ref ProjectileData data, LagCompensatedHit hit, Vector3 direction)
		{
			data.FinishedPosition = hit.Point + direction * _impactPenetration;

			float realDistance = Vector3.Distance(data.FirePosition, hit.Point);
			float hitDamage = _damage.GetDamage(realDistance);

			if (hitDamage > 0f)
			{
				var player = Context.NetworkGame.GetPlayer(Object.InputAuthority);
				var owner = player != null ? player.ActiveAgent : null;

				if (owner != null)
				{
					HitUtility.ProcessHit(owner.Object, direction, hit, hitDamage, _hitType, out HitData hitData);
				}
				else
				{
					HitUtility.ProcessHit(Object.InputAuthority, direction, hit, hitDamage, _hitType, out HitData hitData);
				}
			}

			bool isDynamicTarget = hit.GameObject.layer == ObjectLayer.Agent || hit.GameObject.layer == ObjectLayer.Target;

			if (_spawnImpactOnStaticHitOnly == false || isDynamicTarget == false)
			{
				SpawnImpact(ref data, hit.Point, (hit.Normal + -direction) * 0.5f, hit.GameObject.tag.GetHashCode());
			}

			data.HasStopped = true;
			data.DespawnCooldown = TickTimer.CreateFromSeconds(Runner, isDynamicTarget == false ? _impactDespawnTime : 0.1f);
		}

		private void ProcessBounce(ref ProjectileData data, LagCompensatedHit hit, Vector3 direction, float distance)
		{
			float bounceMultiplier = Mathf.Lerp(_bounceVelocityMultiplier, 0.9f, _bounceCount / 8f);

			// Stop bouncing when the velocity is small enough
			if (distance * bounceMultiplier < _bounceObjectRadius * 2f)
			{
				data.HasStopped = true;
				data.FinishedPosition = hit.Point + Vector3.Reflect(direction, hit.Normal) * _bounceObjectRadius;
				return;
			}

			float distanceToHit = Vector3.Distance(hit.Point, transform.position);
			float progressToHit = distanceToHit / distance;

			var reflectedDirection = Vector3.Reflect(direction, hit.Normal);

			data.FirePosition = hit.Point + reflectedDirection * _bounceObjectRadius;;
			data.InitialVelocity = reflectedDirection * data.InitialVelocity.magnitude * bounceMultiplier;

			// Simple trick to better align position with ticks. More precise solution would be to remember
			// alpha between ticks (when the bounce happened) but it is good enough here.
			data.StartTick = progressToHit > 0.5f ? Runner.Tick : Runner.Tick - 1;

			_bounceCount++;
		}

		private void SpawnImpact(ref ProjectileData data, Vector3 position, Vector3 normal, int impactTagHash)
		{
			if (position == Vector3.zero)
				return;

			data.ImpactPosition = position;
			data.ImpactNormal   = normal;
			data.ImpactTagHash  = impactTagHash;
			data.HasImpacted    = true;

			if (_impactEffect != null)
			{
				var networkBehaviour = _impactEffect.GetComponent<NetworkBehaviour>();
				if (networkBehaviour != null)
				{
					if (HasStateAuthority == true)
					{
						Runner.Spawn(networkBehaviour, position, Quaternion.LookRotation(normal), Object.InputAuthority);
					}
				}
				else
				{
					var effect = Context.ObjectCache.Get(_impactEffect);
					effect.transform.SetPositionAndRotation(position, Quaternion.LookRotation(normal));
				}
			}

			if (_impactSetup != null && impactTagHash != 0)
			{
				var impactParticle = Context.ObjectCache.Get(_impactSetup.GetImpact(impactTagHash));
				Context.ObjectCache.ReturnDeferred(impactParticle, 5f);
				Runner.MoveToRunnerSceneExtended(impactParticle);

				impactParticle.transform.position = position;
				impactParticle.transform.rotation = Quaternion.LookRotation(normal);
			}

			_hasImpactedVisual = true;
		}

		private Vector3 GetProjectilePosition(ref ProjectileData data, float tick)
		{
			float time = (tick - data.StartTick) * Runner.DeltaTime;

			if (time <= 0f)
				return data.FirePosition;

			return data.FirePosition + data.InitialVelocity * time + new Vector3(0f, -_gravity, 0f) * time * time * 0.5f;
		}

		// CLASSES / STRUCTS

		public struct ProjectileData : INetworkStruct
		{
			public bool        IsFinished         => HasImpacted || HasStopped;
			public bool        HasStopped         { get { return State.IsBitSet(0); } set { State.SetBit(0, value); } }
			public bool        HasImpacted        { get { return State.IsBitSet(1); } set { State.SetBit(1, value); } }

			public byte        State;
			public int         StartTick;
			public TickTimer   DespawnCooldown;
			public Vector3     FirePosition;
			public Vector3     InitialVelocity;

			public Vector3     FinishedPosition;

			public Vector3     ImpactPosition;
			public Vector3     ImpactNormal;
			public int         ImpactTagHash;
		}
	}
}
