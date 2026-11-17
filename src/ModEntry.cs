using System;

namespace StolenMeat
{
    /// <summary>Loader-facing lifecycle and ownership of event subscriptions.</summary>
    public sealed class ModEntry : IMod
    {
        private ModLog log;
        private LootObserver observer;
        private OverflowCoordinator overflow;
        private bool enabled;

        /// <summary>Creates the shared logger and feature coordinators.</summary>
        public void Init(ModManifest manifest)
        {
            log = new ModLog(manifest);
            observer = new LootObserver(log);
            overflow = new OverflowCoordinator(observer);
            log.Log($"[{DateTimeOffset.UtcNow:O}] initialized");
        }

        /// <summary>Registers native hooks and enables observer callbacks.</summary>
        public void OnEnable()
        {
            if (enabled) return;
            RatButcherPatch.Observer = observer;
            OverflowPatch.Coordinator = overflow;
            observer.Enable();
            ModHook.OnWakingUpEarly += OnNewDay;
            enabled = true;
        }

        private void OnNewDay()
        {
            overflow.CloseAndClear("new day");
            AnimalWelfareCoordinator.ResetDay();
        }

        /// <summary>Releases hooks and any temporary inventory contents.</summary>
        public void OnDisable()
        {
            if (!enabled) return;
            ModHook.OnWakingUpEarly -= OnNewDay;
            AnimalWelfareCoordinator.ResetDay();
            overflow.CloseAndClear("mod disabled");
            observer.Disable();
            OverflowPatch.Coordinator = null;
            RatButcherPatch.Observer = null;
            enabled = false;
        }
    }
}
