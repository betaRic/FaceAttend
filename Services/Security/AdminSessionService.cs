using System;
using System.Web;
using System.Web.SessionState;
using FaceAttend.Services;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Manages the admin session authentication state.
    /// Session key constants are kept here so no other class needs to know them.
    /// </summary>
    public static class AdminSessionService
    {
        private const string KeyAuthedUtc = "AdminAuthedUtc";
        private const string KeyAdminId   = "AdminId";

        public static void MarkAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session[KeyAuthedUtc] = DateTime.UtcNow;
        }

        public static void MarkAuthed(HttpSessionStateBase session, int adminId)
        {
            if (session == null) return;
            session[KeyAuthedUtc] = DateTime.UtcNow;
            session[KeyAdminId]   = adminId;
        }

        public static int GetAdminId(HttpSessionStateBase session)
        {
            if (session == null) return 1;
            var id = session[KeyAdminId];
            if (id is int intId) return intId;
            return 1;
        }

        public static void ClearAuthed(HttpSessionStateBase session)
        {
            if (session == null) return;
            session.Remove(KeyAuthedUtc);
            session.Remove(KeyAdminId);
            session.Abandon();
        }

        /// <summary>Slides the admin session expiry window forward.</summary>
        public static bool RefreshSession(HttpSessionStateBase session)
        {
            if (session == null) return false;
            if (!(session[KeyAuthedUtc] is DateTime)) return false;
            session[KeyAuthedUtc] = DateTime.UtcNow;
            return true;
        }

        /// <summary>Returns remaining seconds before the admin session expires.</summary>
        public static int GetRemainingSessionSeconds(HttpSessionStateBase session)
        {
            if (session == null) return 0;
            if (!(session[KeyAuthedUtc] is DateTime authedUtc)) return 0;
            var minutes   = ConfigurationService.GetInt("Admin:SessionMinutes", 30);
            var elapsed   = DateTime.UtcNow - authedUtc;
            var remaining = TimeSpan.FromMinutes(minutes) - elapsed;
            return remaining.TotalSeconds > 0 ? (int)remaining.TotalSeconds : 0;
        }

        /// <summary>
        /// Returns true if the current session is authenticated and not expired.
        /// </summary>
        public static bool IsAuthed(HttpSessionStateBase session)
        {
            if (session == null) return false;
            if (!(session[KeyAuthedUtc] is DateTime authedUtc)) return false;
            var minutes = ConfigurationService.GetInt("Admin:SessionMinutes", 30);
            return (DateTime.UtcNow - authedUtc) <= TimeSpan.FromMinutes(minutes);
        }

        /// <summary>
        /// Rotates the ASP.NET session ID to reduce session fixation risk.
        /// Use the short-lived unlock cookie for the next request after calling this.
        /// </summary>
        public static void RotateSessionId(HttpContextBase httpContext)
        {
            if (httpContext?.ApplicationInstance?.Context == null) return;
            try
            {
                var manager = new SessionIDManager();
                bool redirected, cookieAdded;
                var newId = manager.CreateSessionID(httpContext.ApplicationInstance.Context);
                manager.SaveSessionID(httpContext.ApplicationInstance.Context, newId, out redirected, out cookieAdded);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
