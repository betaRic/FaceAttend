/**
 * FaceAttend v2 - Unified UI Components
 * Single file containing all UI modules
 * Version: 2.0
 */

var FaceAttend = window.FaceAttend || {};
FaceAttend.UI = FaceAttend.UI || {};

/**
 * Wizard Component - Multi-step navigation
 */
FaceAttend.UI.Wizard = {
    init: function(containerId, options) {
        var instance = {
            container: document.getElementById(containerId),
            options: Object.assign({
                steps: [],
                currentStep: 1,
                allowBack: true,
                onStepChange: null
            }, options),
            currentStep: 1
        };

        if (!instance.container) {
            console.error('[Wizard] Container not found:', containerId);
            return null;
        }

        instance.currentStep = instance.options.currentStep;
        instance.container._wizardInstance = instance;
        
        return instance;
    },

    get: function(containerId) {
        var container = document.getElementById(containerId);
        return container ? container._wizardInstance : null;
    },

    goTo: function(containerId, step) {
        var instance = this.get(containerId);
        if (!instance) return;

        var totalSteps = instance.container.querySelectorAll('.fa-wizard__step').length;
        if (step < 1 || step > totalSteps) return;

        instance.currentStep = step;
        this._updateUI(instance);
        
        if (instance.options.onStepChange) {
            instance.options.onStepChange(step);
        }
    },

    next: function(containerId) {
        var instance = this.get(containerId);
        if (!instance) return;
        var totalSteps = instance.container.querySelectorAll('.fa-wizard__step').length;
        if (instance.currentStep < totalSteps) {
            this.goTo(containerId, instance.currentStep + 1);
        }
    },

    prev: function(containerId) {
        var instance = this.get(containerId);
        if (!instance) return;
        if (instance.options.allowBack && instance.currentStep > 1) {
            this.goTo(containerId, instance.currentStep - 1);
        }
    },

    markDone: function(containerId, step) {
        var instance = this.get(containerId);
        if (!instance) return;

        var stepEl = instance.container.querySelector('[data-step="' + step + '"]');
        if (stepEl) {
            stepEl.classList.add('is-done');
            var numEl = stepEl.querySelector('.fa-wizard__number');
            if (numEl) numEl.innerHTML = '<i class="fa-solid fa-check"></i>';
            
            var dividers = instance.container.querySelectorAll('.fa-wizard__divider');
            if (dividers[step - 1]) dividers[step - 1].classList.add('is-done');
        }
    },

    _updateUI: function(instance) {
        var steps = instance.container.querySelectorAll('.fa-wizard__step');
        steps.forEach(function(el) {
            el.classList.remove('is-active');
        });
        
        var activeStep = instance.container.querySelector('[data-step="' + instance.currentStep + '"]');
        if (activeStep) activeStep.classList.add('is-active');
        
        instance.container.dataset.current = instance.currentStep;
    }
};

/**
 * Uploader Component - File upload with drag-drop
 */
FaceAttend.UI.Uploader = {
    init: function(containerId, options) {
        var instance = {
            container: document.getElementById(containerId),
            options: Object.assign({
                maxFiles: 5,
                maxSize: 10 * 1024 * 1024,
                allowedTypes: ['image/jpeg', 'image/png'],
                onFilesSelected: null,
                onError: null
            }, options),
            files: []
        };

        if (!instance.container) {
            console.error('[Uploader] Container not found:', containerId);
            return null;
        }

        this._setupElements(instance);
        this._attachEvents(instance);
        instance.container._uploaderInstance = instance;
        
        return instance;
    },

    get: function(containerId) {
        var container = document.getElementById(containerId);
        return container ? container._uploaderInstance : null;
    },

    getFiles: function(containerId) {
        var instance = this.get(containerId);
        return instance ? instance.files : [];
    },

    clear: function(containerId) {
        var instance = this.get(containerId);
        if (!instance) return;

        instance.files = [];
        var input = instance.container.querySelector('.fa-uploader__input');
        if (input) input.value = '';
        var filesContainer = instance.container.querySelector('.fa-uploader__files');
        if (filesContainer) filesContainer.innerHTML = '';
    },

    _setupElements: function(instance) {
        instance.elements = {
            dropzone: instance.container.querySelector('.fa-uploader__dropzone'),
            input: instance.container.querySelector('.fa-uploader__input'),
            files: instance.container.querySelector('.fa-uploader__files'),
            error: instance.container.querySelector('.fa-uploader__error')
        };
    },

    _attachEvents: function(instance) {
        var self = this;
        var dropzone = instance.elements.dropzone;
        var input = instance.elements.input;

        if (!dropzone || !input) return;

        dropzone.addEventListener('click', function(e) {
            if (e.target !== input) input.click();
        });

        input.addEventListener('change', function(e) {
            if (e.target.files.length > 0) {
                self._handleFiles(instance, e.target.files);
            }
        });

        dropzone.addEventListener('dragover', function(e) {
            e.preventDefault();
            this.classList.add('is-dragover');
        });

        dropzone.addEventListener('dragleave', function() {
            this.classList.remove('is-dragover');
        });

        dropzone.addEventListener('drop', function(e) {
            e.preventDefault();
            this.classList.remove('is-dragover');
            if (e.dataTransfer.files.length > 0) {
                self._handleFiles(instance, e.dataTransfer.files);
            }
        });
    },

    _handleFiles: function(instance, fileList) {
        var files = Array.prototype.slice.call(fileList, 0, instance.options.maxFiles);
        var validFiles = [];
        var errors = [];
        var self = this;

        files.forEach(function(file) {
            if (file.size > instance.options.maxSize) {
                errors.push(file.name + ': File too large');
                return;
            }
            validFiles.push(file);
        });

        instance.files = validFiles;
        this._renderFiles(instance);

        if (validFiles.length > 0 && instance.options.onFilesSelected) {
            instance.options.onFilesSelected(validFiles);
        }
    },

    _renderFiles: function(instance) {
        var container = instance.elements.files;
        if (!container) return;

        container.innerHTML = '';
        instance.files.forEach(function(file) {
            var chip = document.createElement('div');
            chip.className = 'fa-uploader__chip';
            chip.innerHTML = '<i class="fa-solid fa-image"></i><span>' + 
                (file.name.length > 20 ? file.name.substring(0, 20) + '...' : file.name) + '</span>';
            container.appendChild(chip);
        });
    }
};

/**
 * Modal Component - Overlay dialogs
 * @deprecated Use FaceAttend.Notify.confirm(), successModal(), errorModal() instead
 */
FaceAttend.UI.Modal = {
    show: function(modalId, options) {
        var modal = document.getElementById(modalId);
        if (!modal) {
            console.error('[Modal] Modal not found:', modalId);
            return;
        }

        options = options || {};
        modal.classList.remove('d-none');
        modal.classList.add('is-open');
        
        if (!options.allowScroll) {
            document.body.style.overflow = 'hidden';
        }

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
    },

    hide: function(modalId, options) {
        var modal = document.getElementById(modalId);
        if (!modal) return;

        options = options || {};
        modal.classList.remove('is-open');
        
        setTimeout(function() {
            modal.classList.add('d-none');
            document.body.style.overflow = '';
        }, options.immediate ? 0 : 300);
    },

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
    },

    showProcessing: function(modalId, options) {
        options = options || {};
        this.show(modalId, {
            title: options.title || 'Processing...',
            status: options.status || 'Please wait...',
            percent: options.percent || 0,
            allowScroll: false
        });
    },

    alert: function(title, message, type) {
        type = type || 'info';
        if (typeof Swal !== 'undefined') {
            Swal.fire({ title: title, text: message, icon: type, confirmButtonText: 'OK' });
        } else {
            alert(title + '\n\n' + message);
        }
    },

    confirm: function(options) {
        return new Promise(function(resolve) {
            options = options || {};
            if (typeof Swal !== 'undefined') {
                Swal.fire({
                    title: options.title || 'Confirm',
                    text: options.message || 'Are you sure?',
                    icon: 'question',
                    showCancelButton: true,
                    confirmButtonText: options.confirmText || 'Yes',
                    cancelButtonText: options.cancelText || 'Cancel'
                }).then(function(result) {
                    resolve(result.isConfirmed);
                });
            } else {
                resolve(confirm(options.message || 'Are you sure?'));
            }
        });
    },

    _updateProgress: function(modal, percent) {
        var bar = modal.querySelector('.fa-modal__progress-bar');
        var text = modal.querySelector('.fa-modal__percent');

        if (bar) bar.style.width = percent + '%';
        if (text) text.textContent = Math.round(percent) + '%';
    }
};

// Auto-initialize
document.addEventListener('DOMContentLoaded', function() {
    // Attach modal close handlers
    document.querySelectorAll('[data-modal-close]').forEach(function(btn) {
        btn.addEventListener('click', function() {
            var modal = this.closest('.fa-modal');
            if (modal) FaceAttend.UI.Modal.hide(modal.id);
        });
    });
});
