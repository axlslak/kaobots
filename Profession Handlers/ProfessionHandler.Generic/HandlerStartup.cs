using System;
using AOSharp.Core;

namespace ProfessionHandler.Generic
{
    // Shared by manual entry points and the selected entry point delegated to by Auto.
    public static class HandlerStartup
    {
        private static bool _starting;
        private static bool _started;
        private static Exception _failure;

        public static void EnsureAvailable()
        {
            if (_starting || _started || Combat.ProfessionHandler.Instance != null || GenericProfessionHandler.Handler != null)
                throw new InvalidOperationException("A profession handler is already starting or running. Select Auto OR one manual handler, then eject and inject again.");
        }

        public static void Start(Action initialize)
        {
            EnsureAvailable();
            if (Game.IsNewEngine)
                throw new NotSupportedException("Profession handlers do not support this engine.");

            _starting = true;
            _failure = null;
            try
            {
                initialize();
                if (_failure != null)
                    throw new InvalidOperationException("Profession handler startup failed; eject before retrying.", _failure);
                if (Combat.ProfessionHandler.Instance == null)
                    throw new InvalidOperationException("Profession handler startup did not create a handler.");
                _started = true;
            }
            finally
            {
                // A failed partial startup remains reserved by Handler/Instance until domain unload.
                _starting = false;
            }
        }

        internal static void RecordFailure(Exception exception)
        {
            if (_starting && _failure == null)
                _failure = exception;
        }
    }
}
