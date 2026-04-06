/**
 * audio-manager.js
 * FaceAttend audio management — success and notification sounds.
 *
 * Reads audio paths from body data attributes:
 *   data-audio-success="..."  (path to success.mp3)
 *   data-audio-notif="..."    (path to notif.mp3)
 *
 * Browsers block audio autoplay until user interaction.
 * This module unlocks the audio context on first user interaction
 * and exposes window.FaceAttendAudio for the rest of the app.
 */
(function () {
    'use strict';

    var body      = document.body;
    var successSrc = (body && body.getAttribute('data-audio-success')) || '/Content/audio/success.mp3';
    var notifSrc   = (body && body.getAttribute('data-audio-notif'))   || '/Content/audio/notif.mp3';

    window.FaceAttendAudio = {
        _unlocked:     false,
        _successAudio: null,
        _notifAudio:   null,
        _initialized:  false,

        init: function () {
            if (this._initialized) return;

            this._successAudio = new Audio(successSrc);
            this._notifAudio   = new Audio(notifSrc);
            this._successAudio.load();
            this._notifAudio.load();
            this._initialized = true;
            this._setupAutoUnlock();
        },

        _setupAutoUnlock: function () {
            var self   = this;
            var events = ['click', 'touchstart', 'keydown', 'pointerdown'];
            var handler = function () {
                self.unlock();
                events.forEach(function (e) {
                    document.removeEventListener(e, handler, true);
                });
            };
            events.forEach(function (e) {
                document.addEventListener(e, handler, { once: true, capture: true });
            });
        },

        unlock: function () {
            if (this._unlocked) return Promise.resolve(true);
            var self     = this;
            var promises = [];
            if (this._successAudio) {
                this._successAudio.volume = 0.01;
                promises.push(
                    this._successAudio.play()
                        .then(function () {
                            self._successAudio.pause();
                            self._successAudio.currentTime = 0;
                            self._successAudio.volume = 1.0;
                        })
                        .catch(function () { /* autoplay policy */ })
                );
            }
            return Promise.all(promises).then(function () {
                self._unlocked = true;
                return true;
            }).catch(function () { return false; });
        },

        play: function (sound) {
            if (!this._initialized) this.init();
            var audio = sound === 'notif' ? this._notifAudio : this._successAudio;
            if (!audio) return Promise.resolve(false);
            audio.currentTime = 0;
            return audio.play()
                .then(function () { return true; })
                .catch(function (err) {
                    console.warn('[FaceAttendAudio] play(' + sound + ') failed:', err && err.message);
                    return false;
                });
        },

        playSuccess:      function () { return this.play('success'); },
        playNotification: function () { return this.play('notif');   },
        stop:             function () { /* no-op — one-shot sounds */ },
        isUnlocked:       function () { return this._unlocked; }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () {
            window.FaceAttendAudio.init();
        });
    } else {
        window.FaceAttendAudio.init();
    }
}());
