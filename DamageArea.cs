using UnityEngine;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.KCC;

namespace TPSBR
{
	public sealed class DamageArea : NetworkKCCProcessor
	{
		// PRIVATE MEMBERS

		[SerializeField]
		private float _damagePerSecond = 20f;
		[SerializeField]
		private int _hitsPerSecond = 4;

		[Networked]
		private TickTimer _cooldown { get; set; }

		private HashSet<IHitTarget> _targets = new HashSet<IHitTarget>();

		// NetworkKCCProcessor INTERFACE

		public override void OnEnter(KCC kcc, KCCData data)
		{
			if (kcc.IsInFixedUpdate == false || HasStateAuthority == false)
				return;

			var target = kcc.GetComponent<IHitTarget>();
			if (target != null)
			{
				_targets.Add(target);
			}
		}

		public override void OnExit(KCC kcc, KCCData data)
		{
			if (kcc.IsInFixedUpdate == false || HasStateAuthority == false)
				return;

			var target = kcc.GetComponent<IHitTarget>();
			if (target != null)
			{
				_targets.Remove(target);
			}
		}

		public override void FixedUpdateNetwork()
		{
			if (HasStateAuthority == false)
				return;

			if (_damagePerSecond <= 0f)
				return;

			if (_cooldown.ExpiredOrNotRunning(Runner) == true)
			{
				Fire();
			}
		}

		// PRIVATE METHODS

		private void Fire()
		{
			_cooldown = TickTimer.CreateFromSeconds(Runner, 1f / _hitsPerSecond);
			float damage = _damagePerSecond  / _hitsPerSecond;

			foreach (var target in _targets)
			{
				var targetPosition = (target as MonoBehaviour).transform.position;

				HitData hitData = new HitData();
				hitData.Action           = EHitAction.Damage;
				hitData.Amount           = damage;
				hitData.Position         = targetPosition;
				hitData.InstigatorRef    = Object.InputAuthority;
				hitData.Direction        = (targetPosition - transform.position).normalized;
				hitData.Normal           = Vector3.up;
				hitData.Target           = target;
				hitData.HitType          = EHitType.Suicide;

				HitUtility.ProcessHit(ref hitData);
			}
		}
	}
}
