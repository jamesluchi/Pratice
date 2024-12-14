using System;
using Fusion;

namespace TPSBR
{
	using UnityEngine;

	public struct BodyHitData : INetworkStruct
	{
		public EHitAction Action;
		public float      Damage;
		public Vector3    RelativePosition;
		public Vector3    Direction;
		public PlayerRef  Instigator;
	}

	public sealed class Health : ContextBehaviour, IHitTarget, IHitInstigator
	{
		// PUBLIC MEMBERS

		/// <summary>
		/// 에이전트가 살아있는지 여부를 나타냄
		/// </summary>
		public bool  IsAlive   => CurrentHealth > 0f;
		/// <summary>
		/// 최대 체력을 나타냄
		/// </summary>
		public float MaxHealth => _maxHealth;
		/// <summary>
		/// 최대 실드를 나타냄
		/// </summary>
		public float MaxShield => _maxShield;

		[Networked, HideInInspector]
		/// <summary>
		/// 현재 체력을 나타냄
		/// </summary>
		public float CurrentHealth { get; private set; }
		[Networked, HideInInspector]
		/// <summary>
		/// 현재 실드를 나타냄
		/// </summary>
		public float CurrentShield { get; private set; }

		/// <summary>
		/// 히트를 받았을 때 발생하는 이벤트
		/// </summary>
		public event Action<HitData> HitTaken;
		/// <summary>
		/// 히트를 가한 후 발생하는 이벤트
		/// </summary>
		public event Action<HitData> HitPerformed;

		// PRIVATE MEMBERS

		[SerializeField]
		/// <summary>
		/// 최대 체력을 나타냄
		/// </summary>
		private float _maxHealth;
		[SerializeField]
		/// <summary>
		/// 최대 실드를 나타냄
		/// </summary>
		private float _maxShield;
		[SerializeField]
		/// <summary>
		/// 시작 실드를 나타냄
		/// </summary>
		private float _startShield;
		[SerializeField]
		/// <summary>
		/// 히트 인디케이터의 피벗을 나타냄
		/// </summary>
		private Transform _hitIndicatorPivot;

		[Header("Regeneration")]
		[SerializeField]
		/// <summary>
		/// 초당 체력 자동 회복량을 나타냄
		/// </summary>
		private float _healthRegenPerSecond;
		[SerializeField]
		/// <summary>
		/// 최대 체력 자동 회복량을 나타냄
		/// </summary>
		private float _maxHealthFromRegen;
		[SerializeField]
		/// <summary>
		/// 초당 체력 자동 회복 틱 수를 나타냄
		/// </summary>
		private int _regenTickPerSecond;
		[SerializeField]
		/// <summary>
		/// 전투 지연 시간을 나타냄
		/// </summary>
		private int _regenCombatDelay;

		[Networked]
		/// <summary>
		/// 히트 카운트를 나타냄
		/// </summary>
		private int _hitCount { get; set; }
		[Networked, Capacity(4)]
		/// <summary>
		/// 히트 데이터를 나타냄
		/// </summary>
		private NetworkArray<BodyHitData> _hitData { get; }

		private int _visibleHitCount;
		private Agent _agent;

		private TickTimer _regenTickTimer;
		private float _healthRegenPerTick;
		private float _regenTickTime;

		// PUBLIC METHODS

		/// <summary>
		/// 에이전트가 스폰될 때 호출되는 메서드
		/// 현재 히트 카운트를 시각적 히트 카운트에 동기화
		/// </summary>
		public void OnSpawned(Agent agent)
		{
			_visibleHitCount = _hitCount;
		}

		/// <summary>
		/// 에이전트가 디스폰될 때 호출되는 메서드
		/// 히트 이벤트 리스너들을 제거
		/// </summary>
		public void OnDespawned()
		{
			HitTaken = null;
			HitPerformed = null;
		}

		/// <summary>
		/// 매 고정 업데이트마다 실행되는 메서드
		/// 체력 자동 회복 기능을 처리
		/// </summary>
		public void OnFixedUpdate()
		{
			if (HasStateAuthority == false)
				return;

			if (IsAlive == true && _healthRegenPerSecond > 0f && _regenTickTimer.ExpiredOrNotRunning(Runner) == true)
			{
				_regenTickTimer = TickTimer.CreateFromSeconds(Runner, _regenTickTime);

				var healthDiff = _maxHealthFromRegen - CurrentHealth;
				if (healthDiff <= 0f)
					return;

				AddHealth(Mathf.Min(healthDiff, _healthRegenPerTick));
			}
		}

		/// <summary>
		/// 체력 회복 딜레이를 리셋하는 메서드
		/// 주로 데미지를 받았을 때 호출됨
		/// </summary>
		public void ResetRegenDelay()
		{
			_regenTickTimer = TickTimer.CreateFromSeconds(Runner, _regenCombatDelay);
		}

		/// <summary>
		/// 네트워크 상태 초기화 시 호출되는 메서드
		/// 최대 체력과 시작 실드 값으로 초기화
		/// </summary>
		public override void CopyBackingFieldsToState(bool firstTime)
		{
			base.CopyBackingFieldsToState(firstTime);

			InvokeWeavedCode();

			CurrentHealth = _maxHealth;
			CurrentShield = _startShield;
		}

		// NetworkBehaviour INTERFACE

		/// <summary>
		/// 매 프레임 렌더링 시 호출되는 메서드
		/// 클라이언트에서 시각적 히트 효과를 업데이트
		/// </summary>
		public override void Render()
		{
			if (Runner.Mode != SimulationModes.Server)
			{
				UpdateVisibleHits();
			}
		}

		// MONOBEHAVIOUR

		/// <summary>
		/// 컴포넌트 초기화 시 호출되는 메서드
		/// 체력 회복 관련 값들을 초기화
		/// </summary>
		private void Awake()
		{
			_agent = GetComponent<Agent>();

			_regenTickTime      = 1f / _regenTickPerSecond;
			_healthRegenPerTick = _healthRegenPerSecond / _regenTickPerSecond;
		}

		// IHitTarget INTERFACE

		/// <summary>
		/// 히트 피벗을 나타냄
		/// </summary>
		Transform IHitTarget.HitPivot => _hitIndicatorPivot != null ? _hitIndicatorPivot : transform;

		/// <summary>
		/// 히트 데이터를 처리하는 메서드
		/// 데미지 적용 및 사망 처리를 담당
		/// </summary>
		void IHitTarget.ProcessHit(ref HitData hitData)
		{
			if (IsAlive == false)
			{
				hitData.Amount = 0;
				return;
			}

			ApplyHit(ref hitData);

			if (IsAlive == false)
			{
				hitData.IsFatal = true;
				Context.GameplayMode.AgentDeath(_agent, hitData);
			}
		}

		// IHitInstigator INTERFACE

		/// <summary>
		/// 히트를 가한 후 호출되는 메서드
		/// 히트 이벤트를 발생시킴
		/// </summary>
		void IHitInstigator.HitPerformed(HitData hitData)
		{
			if (hitData.Amount > 0 && hitData.Target != (IHitTarget)this && Runner.IsResimulation == false)
			{
				HitPerformed?.Invoke(hitData);
			}
		}

		// PRIVATE METHODS

		/// <summary>
		/// 실제 히트를 적용하는 내부 메서드
		/// 데미지, 힐링, 실드 효과를 처리
		/// </summary>
		private void ApplyHit(ref HitData hit)
		{
			if (IsAlive == false)
				return;

			if (hit.Action == EHitAction.Damage)
			{
				hit.Amount = ApplyDamage(hit.Amount);
			}
			else if (hit.Action == EHitAction.Heal)
			{
				hit.Amount = AddHealth(hit.Amount);
			}
			else if (hit.Action == EHitAction.Shield)
			{
				hit.Amount = AddShield(hit.Amount);
			}

			if (hit.Amount <= 0)
				return;

			// Hit taken effects (blood) is shown immediately for local player, for other
			// effects (hit number, crosshair hit effect) we are waiting for server confirmation
			if (hit.InstigatorRef == Context.LocalPlayerRef && Runner.IsForward == true)
			{
				HitTaken?.Invoke(hit);
			}

			if (HasStateAuthority == false)
				return;

			_hitCount++;

			var bodyHitData = new BodyHitData
			{
				Action           = hit.Action,
				Damage           = hit.Amount,
				Direction        = hit.Direction,
				RelativePosition = hit.Position != Vector3.zero ? hit.Position - transform.position : Vector3.zero,
				Instigator       = hit.InstigatorRef,
			};

			int hitIndex = _hitCount % _hitData.Length;
			_hitData.Set(hitIndex, bodyHitData);
		}

		/// <summary>
		/// 데미지를 적용하는 메서드
		/// 실드와 체력에 데미지를 순차적으로 적용
		/// </summary>
		private float ApplyDamage(float damage)
		{
			if (damage <= 0f)
				return 0f;

			ResetRegenDelay();

			var shieldChange = AddShield(-damage);
			var healthChange = AddHealth(-(damage + shieldChange));

			return -(shieldChange + healthChange);
		}

		/// <summary>
		/// 체력을 증가/감소시키는 메서드
		/// 변경된 체력량을 반환
		/// </summary>
		private float AddHealth(float health)
		{
			float previousHealth = CurrentHealth;
			SetHealth(CurrentHealth + health);
			return CurrentHealth - previousHealth;
		}

		/// <summary>
		/// 실드를 증가/감소시키는 메서드
		/// 변경된 실드량을 반환
		/// </summary>
		private float AddShield(float shield)
		{
			float previousShield = CurrentShield;
			SetShield(CurrentShield + shield);
			return CurrentShield - previousShield;
		}

		/// <summary>
		/// 체력을 설정하는 메서드
		/// 0과 최대체력 사이로 제한됨
		/// </summary>
		private void SetHealth(float health)
		{
			CurrentHealth = Mathf.Clamp(health, 0, _maxHealth);
		}

		/// <summary>
		/// 실드를 설정하는 메서드
		/// 0과 최대실드 사이로 제한됨
		/// </summary>
		private void SetShield(float shield)
		{
			CurrentShield = Mathf.Clamp(shield, 0, _maxShield);
		}

		/// <summary>
		/// 시각적 히트 효과를 업데이트하는 메서드
		/// 네트워크로 동기화된 히트 데이터를 처리
		/// </summary>
		private void UpdateVisibleHits()
		{
			if (_visibleHitCount == _hitCount)
				return;

			int dataCount = _hitData.Length;
			int oldestHitData = _hitCount - dataCount + 1;

			for (int i = Mathf.Max(_visibleHitCount + 1, oldestHitData); i <= _hitCount; i++)
			{
				int shotIndex = i % dataCount;
				var bodyHitData = _hitData.Get(shotIndex);

				var hitData = new HitData
				{
					Action        = bodyHitData.Action,
					Amount        = bodyHitData.Damage,
					Position      = transform.position + bodyHitData.RelativePosition,
					Direction     = bodyHitData.Direction,
					Normal        = -bodyHitData.Direction,
					Target        = this,
					InstigatorRef = bodyHitData.Instigator,
					IsFatal       = i == _hitCount && CurrentHealth <= 0f,
				};

				OnHitTaken(hitData);
			}

			_visibleHitCount = _hitCount;
		}

		/// <summary>
		/// 히트를 받았을 때 호출되는 메서드
		/// 히트 이벤트를 발생시키고 공격자에게 피드백을 제공
		/// </summary>
		private void OnHitTaken(HitData hit)
		{
			// For local player, HitTaken was already called when applying hit
			if (hit.InstigatorRef != Context.LocalPlayerRef)
			{
				HitTaken?.Invoke(hit);
			}

			// We use _hitData buffer to inform instigator about successful hit as this needs
			// to be synchronized over network as well (e.g. when spectating other players)
			if (hit.InstigatorRef.IsRealPlayer == true && hit.InstigatorRef == Context.ObservedPlayerRef)
			{
				var instigator = hit.Instigator;

				if (instigator == null)
				{
					var player = Context.NetworkGame.GetPlayer(hit.InstigatorRef);
					instigator = player != null ? player.ActiveAgent.Health as IHitInstigator : null;
				}

				if (instigator != null)
				{
					instigator.HitPerformed(hit);
				}
			}
		}

		// DEBUG

		/// <summary>
		/// 디버그 메서드: 체력 증가
		/// </summary>
		[ContextMenu("Add Health")]
		private void Debug_AddHealth()
		{
			CurrentHealth += 10;
		}

		/// <summary>
		/// 디버그 메서드: 체력 감소
		/// </summary>
		[ContextMenu("Remove Health")]
		private void Debug_RemoveHealth()
		{
			CurrentHealth -= 10;
		}
	}
}
