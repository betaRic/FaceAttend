using System;
using System.Web;

namespace FaceAttend.Services
{
    /// <summary>
    /// Shared audio management for success sounds and notifications.
    /// 
    /// Browsers block audio autoplay until user interaction.
    /// This manager provides client-side helpers to:
    /// 1. Unlock audio context on first user interaction
    /// 2. Play success.mp3 after attendance
    /// 3. Play notif.mp3 for reminders
    /// 
    /// IMPORTANT: Audio files must be placed in:
    /// - /Content/audio/success.mp3
    /// - /Content/audio/notif.mp3
    /// </summary>
    public static class AudioManager
    {
        /// <summary>
        /// Gets the path to the success sound (played after attendance scan)
        /// </summary>
        public static string SuccessSoundPath => "~/Content/audio/success.mp3";

        /// <summary>
        /// Gets the path to the notification sound (played for reminders)
        /// </summary>
        public static string NotificationSoundPath => "~/Content/audio/notif.mp3";

        /// <summary>
        /// Generates the JavaScript code for the audio manager.
        /// Include this in your layout or page.
        /// </summary>
        public static IHtmlString GetAudioManagerScript(HttpContextBase context)
        {
            var successUrl = VirtualPathUtility.ToAbsolute(SuccessSoundPath);
            var notifUrl = VirtualPathUtility.ToAbsolute(NotificationSoundPath);

            var script = $@"
<script>
(function() {{
    'use strict';
    
    // Audio Manager for FaceAttend
    window.FaceAttendAudio = {{
        _unlocked: false,
        _successAudio: null,
        _notifAudio: null,
        _initialized: false,
        
        // Paths to audio files
        paths: {{
            success: '{successUrl}',
            notif: '{notifUrl}'
        }},
        
        // Initialize audio elements (call on page load)
        init: function() {{
            if (this._initialized) return;
            
            this._successAudio = new Audio(this.paths.success);
            this._notifAudio = new Audio(this.paths.notif);
            
            // Preload audio
            this._successAudio.load();
            this._notifAudio.load();
            
            this._initialized = true;
            
            // Auto-unlock on first interaction
            this._setupAutoUnlock();
        }},
        
        // Setup auto-unlock on first user interaction
        _setupAutoUnlock: function() {{
            var self = this;
            var unlockEvents = ['click', 'touchstart', 'keydown', 'pointerdown'];
            
            var unlockHandler = function() {{
                self.unlock();
                // Remove all listeners after unlock
                unlockEvents.forEach(function(evt) {{
                    document.removeEventListener(evt, unlockHandler, true);
                }});
            }};
            
            unlockEvents.forEach(function(evt) {{
                document.addEventListener(evt, unlockHandler, {{ once: true, capture: true }});
            }});
        }},
        
        // Explicitly unlock audio (call after user interaction like camera permission)
        unlock: function() {{
            if (this._unlocked) return Promise.resolve(true);
            
            var self = this;
            var promises = [];
            
            if (this._successAudio) {{
                this._successAudio.volume = 0.01; // Nearly silent
                promises.push(this._successAudio.play().then(function() {{
                    self._successAudio.pause();
                    self._successAudio.currentTime = 0;
                    self._successAudio.volume = 1.0;
                }}).catch(function() {{ /* Ignore autoplay errors */ }}));
            }}
            
            return Promise.all(promises).then(function() {{
                self._unlocked = true;
                console.log('[FaceAttendAudio] Audio unlocked');
                return true;
            }}).catch(function() {{
                return false;
            }});
        }},
        
        // Play success sound (after attendance)
        playSuccess: function() {{
            if (!this._initialized) this.init();
            if (!this._successAudio) return Promise.resolve(false);
            
            var self = this;
            this._successAudio.currentTime = 0;
            
            return this._successAudio.play().then(function() {{
                console.log('[FaceAttendAudio] Success sound played');
                return true;
            }}).catch(function(err) {{
                console.warn('[FaceAttendAudio] Success sound failed:', err.message);
                return false;
            }});
        }},
        
        // Play notification sound (for reminders)
        playNotification: function() {{
            if (!this._initialized) this.init();
            if (!this._notifAudio) return Promise.resolve(false);
            
            this._notifAudio.currentTime = 0;
            
            return this._notifAudio.play().then(function() {{
                console.log('[FaceAttendAudio] Notification sound played');
                return true;
            }}).catch(function(err) {{
                console.warn('[FaceAttendAudio] Notification sound failed:', err.message);
                return false;
            }});
        }},
        
        // Check if audio is unlocked
        isUnlocked: function() {{
            return this._unlocked;
        }}
    }};
    
    // Auto-initialize on DOM ready
    if (document.readyState === 'loading') {{
        document.addEventListener('DOMContentLoaded', function() {{
            window.FaceAttendAudio.init();
        }});
    }} else {{
        window.FaceAttendAudio.init();
    }}
}})();
</script>";

            return new HtmlString(script);
        }

        /// <summary>
        /// Gets a simple inline script to play success sound
        /// </summary>
        public static IHtmlString PlaySuccessScript()
        {
            return new HtmlString("<script>if(window.FaceAttendAudio)window.FaceAttendAudio.playSuccess();</script>");
        }

        /// <summary>
        /// Gets a simple inline script to play notification sound
        /// </summary>
        public static IHtmlString PlayNotificationScript()
        {
            return new HtmlString("<script>if(window.FaceAttendAudio)window.FaceAttendAudio.playNotification();</script>");
        }
    }
}
