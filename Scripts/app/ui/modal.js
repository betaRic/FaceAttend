/**
 * FaceAttend UI - Modal Component
 * Overlay dialogs and processing screens
 * 
 * @version 2.0
 * @requires FaceAttend core
 */

var FaceAttend = window.FaceAttend || {};
FaceAttend.UI = FaceAttend.UI || {};

/**
 * Modal Component
 * Control for overlay dialogs
 */
FaceAttend.UI.Modal = {
    /**
     * Show a modal
     * @param {string} modalId - Modal element ID
     * @param {Object} options - Optional settings
     */
    show: function(modalId, options) {
        var modal = document.getElementById(modalId);
        if (!modal) {
            console.error('[FaceAttend.UI.Modal] Modal not found:', modalId);
            return;
        }

        options = options || {};

        // Show modal
        modal.classList.remove('d-none');
        modal.classList.add('is-open');
        
        // Prevent body scroll
        if (!options.allowScroll) {
            document.body.style.overflow = 'hidden';
        }

        // Update content if provided
        if (options.title) {
            var titleEl = modal.querySelector('.fa-modal__title');
            if (titleEl) titleEl.textContent = options.title;
        }

        if (options.status) {
            var statusEl = modal.querySelector('.fa-modal__status');
            if (statusEl) statusEl.textContent = options.status;
        }

        if (options.percent !== undefined) {
            this._updateProgress(modal, options.percent);
        }

        // Focus trap for accessibility
        this._setupFocusTrap(modal);

        // Call onShow callback
        if (options.onShow && typeof options.onShow === 'function') {
            setTimeout(options.onShow, 0);
        }
    },

    /**
     * Hide a modal
     * @param {string} modalId - Modal element ID
     * @param {Object} options - Optional settings
     */
    hide: function(modalId, options) {
        var modal = document.getElementById(modalId);
        if (!modal) return;

        options = options || {};

        modal.classList.remove('is-open');
        
        // Wait for animation to complete before hiding
        setTimeout(function() {
            modal.classList.add('d-none');
            
            // Restore body scroll
            document.body.style.overflow = '';
            
            if (options.onHide && typeof options.onHide === 'function') {
                options.onHide();
            }
        }, options.immediate ? 0 : 300);
    },

    /**
     * Update modal content
     * @param {string} modalId - Modal element ID
     * @param {Object} updates - Content updates
     */
    update: function(modalId, updates) {
        var modal = document.getElementById(modalId);
        if (!modal) return;

        if (updates.title) {
            var titleEl = modal.querySelector('.fa-modal__title');
            if (titleEl) titleEl.textContent = updates.title;
        }

        if (updates.status) {
            var statusEl = modal.querySelector('.fa-modal__status');
            if (statusEl) statusEl.textContent = updates.status;
        }

        if (updates.percent !== undefined) {
            this._updateProgress(modal, updates.percent);
        }

        if (updates.message) {
            var msgEl = modal.querySelector('.fa-modal__message');
            if (msgEl) msgEl.textContent = updates.message;
        }
    },

    /**
     * Show processing modal with progress
     * @param {string} modalId - Modal element ID
     * @param {Object} options - Options
     */
    showProcessing: function(modalId, options) {
        options = options || {};
        this.show(modalId, {
            title: options.title || 'Processing...',
            status: options.status || 'Please wait...',
            percent: options.percent || 0,
            allowScroll: false
        });
    },

    /**
     * Update processing progress
     * @param {string} modalId - Modal element ID
     * @param {number} percent - Progress percentage (0-100)
     * @param {string} status - Status message
     */
    updateProgress: function(modalId, percent, status) {
        this.update(modalId, {
            percent: percent,
            status: status
        });
    },

    /**
     * Show success modal
     * @param {string} modalId - Modal element ID or 'success' for default
     * @param {Object} options - Options
     */
    showSuccess: function(modalId, options) {
        if (modalId === 'success') {
            // Use default success modal or create one
            modalId = this._ensureSuccessModal();
        }

        options = options || {};
        var modal = document.getElementById(modalId);
        if (!modal) return;

        // Add success class
        modal.classList.add('fa-modal--success');

        // Update content
        if (options.title) {
            var titleEl = modal.querySelector('.fa-modal__title');
            if (titleEl) titleEl.textContent = options.title;
        }

        if (options.message) {
            var msgEl = modal.querySelector('.fa-modal__message');
            if (msgEl) msgEl.innerHTML = options.message;
        }

        if (options.details) {
            var detailsEl = modal.querySelector('.fa-modal__details');
            if (detailsEl) detailsEl.textContent = options.details;
        }

        this.show(modalId, { allowScroll: false });
    },

    /**
     * Confirm dialog
     * @param {Object} options - Confirm options
     * @returns {Promise} Resolves with boolean
     */
    confirm: function(options) {
        return new Promise(function(resolve) {
            options = options || {};
            
            var title = options.title || 'Confirm';
            var message = options.message || 'Are you sure?';
            var confirmText = options.confirmText || 'Yes';
            var cancelText = options.cancelText || 'Cancel';

            // Use SweetAlert if available
            if (typeof Swal !== 'undefined') {
                Swal.fire({
                    title: title,
                    text: message,
                    icon: 'question',
                    showCancelButton: true,
                    confirmButtonText: confirmText,
                    cancelButtonText: cancelText,
                    reverseButtons: true
                }).then(function(result) {
                    resolve(result.isConfirmed);
                });
            } else {
                // Fallback to native confirm
                resolve(confirm(message));
            }
        });
    },

    /**
     * Alert dialog
     * @param {string} title - Alert title
     * @param {string} message - Alert message
     * @param {string} type - Alert type (success, error, warning, info)
     */
    alert: function(title, message, type) {
        type = type || 'info';

        // Use SweetAlert if available
        if (typeof Swal !== 'undefined') {
            Swal.fire({
                title: title,
                text: message,
                icon: type,
                confirmButtonText: 'OK'
            });
        } else {
            // Fallback to native alert
            alert(title + '\n\n' + message);
        }
    },

    /**
     * Internal: Update progress bar
     * @private
     */
    _updateProgress: function(modal, percent) {
        var bar = modal.querySelector('.fa-modal__progress-bar');
        var text = modal.querySelector('.fa-modal__percent');

        if (bar) {
            bar.style.width = percent + '%';
        }

        if (text) {
            text.textContent = Math.round(percent) + '%';
        }
    },

    /**
     * Internal: Setup focus trap for accessibility
     * @private
     */
    _setupFocusTrap: function(modal) {
        var focusableElements = modal.querySelectorAll(
            'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'
        );

        if (focusableElements.length === 0) return;

        var firstElement = focusableElements[0];
        var lastElement = focusableElements[focusableElements.length - 1];

        // Focus first element
        setTimeout(function() {
            firstElement.focus();
        }, 100);

        // Trap focus
        modal.addEventListener('keydown', function(e) {
            if (e.key !== 'Tab') return;

            if (e.shiftKey) {
                if (document.activeElement === firstElement) {
                    e.preventDefault();
                    lastElement.focus();
                }
            } else {
                if (document.activeElement === lastElement) {
                    e.preventDefault();
                    firstElement.focus();
                }
            }
        });

        // Close on escape
        modal.addEventListener('keydown', function(e) {
            if (e.key === 'Escape') {
                var closeBtn = modal.querySelector('.fa-modal__close');
                if (closeBtn) {
                    closeBtn.click();
                }
            }
        });
    },

    /**
     * Internal: Ensure success modal exists
     * @private
     */
    _ensureSuccessModal: function() {
        var modalId = 'faModalSuccess';
        var existing = document.getElementById(modalId);
        if (existing) return modalId;

        var modal = document.createElement('div');
        modal.id = modalId;
        modal.className = 'fa-modal fa-modal--success d-none';
        modal.innerHTML = 
            '<div class="fa-modal__backdrop"></div>' +
            '<div class="fa-modal__content">' +
                '<div class="fa-modal__success-icon"><i class="fa-solid fa-circle-check"></i></div>' +
                '<div class="fa-modal__title">Success!</div>' +
                '<div class="fa-modal__message"></div>' +
                '<div class="fa-modal__details"></div>' +
                '<div class="fa-modal__actions">' +
                    '<button type="button" class="btn btn-primary" onclick="FaceAttend.UI.Modal.hide(\'' + modalId + '\')">OK</button>' +
                '</div>' +
            '</div>';

        document.body.appendChild(modal);
        return modalId;
    }
};

// Auto-initialize modals with data attributes
document.addEventListener('DOMContentLoaded', function() {
    // Find all modals with auto-show
    var autoModals = document.querySelectorAll('[data-modal-show="true"]');
    autoModals.forEach(function(modal) {
        FaceAttend.UI.Modal.show(modal.id);
    });

    // Attach close button handlers
    document.querySelectorAll('.fa-modal__close, [data-modal-close]').forEach(function(btn) {
        btn.addEventListener('click', function() {
            var modal = this.closest('.fa-modal');
            if (modal) {
                FaceAttend.UI.Modal.hide(modal.id);
            }
        });
    });

    // Attach backdrop click handlers
    document.querySelectorAll('.fa-modal').forEach(function(modal) {
        var backdrop = modal.querySelector('.fa-modal__backdrop');
        if (backdrop) {
            backdrop.addEventListener('click', function() {
                if (!modal.classList.contains('fa-modal--no-backdrop-close')) {
                    FaceAttend.UI.Modal.hide(modal.id);
                }
            });
        }
    });
});
