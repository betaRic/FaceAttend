/**
 * FaceAttend UI - Wizard Component
 * Multi-step navigation control
 * 
 * @version 2.0
 * @requires FaceAttend core
 */

var FaceAttend = window.FaceAttend || {};
FaceAttend.UI = FaceAttend.UI || {};

/**
 * Wizard Component
 * Programmatic control for multi-step flows
 */
FaceAttend.UI.Wizard = {
    /**
     * Initialize wizard
     * @param {string} containerId - Wizard container element ID
     * @param {Object} options - Configuration options
     * @returns {Object} Wizard instance
     */
    init: function(containerId, options) {
        var instance = {
            container: document.getElementById(containerId),
            options: Object.assign({
                steps: [],
                currentStep: 1,
                allowBack: true,
                onStepChange: null,
                onComplete: null
            }, options),
            currentStep: 1
        };

        if (!instance.container) {
            console.error('[FaceAttend.UI.Wizard] Container not found:', containerId);
            return null;
        }

        instance.currentStep = instance.options.currentStep;
        this._render(instance);
        this._attachEvents(instance);
        
        // Store reference
        instance.container._wizardInstance = instance;
        
        return instance;
    },

    /**
     * Get wizard instance by container ID
     * @param {string} containerId 
     * @returns {Object} Wizard instance
     */
    get: function(containerId) {
        var container = document.getElementById(containerId);
        return container ? container._wizardInstance : null;
    },

    /**
     * Navigate to specific step
     * @param {string} containerId - Wizard container ID
     * @param {number} step - Step number (1-based)
     */
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

        if (step === totalSteps && instance.options.onComplete) {
            instance.options.onComplete();
        }
    },

    /**
     * Go to next step
     * @param {string} containerId - Wizard container ID
     */
    next: function(containerId) {
        var instance = this.get(containerId);
        if (!instance) return;
        
        var totalSteps = instance.container.querySelectorAll('.fa-wizard__step').length;
        if (instance.currentStep < totalSteps) {
            this.goTo(containerId, instance.currentStep + 1);
        }
    },

    /**
     * Go to previous step
     * @param {string} containerId - Wizard container ID
     */
    prev: function(containerId) {
        var instance = this.get(containerId);
        if (!instance) return;
        
        if (instance.options.allowBack && instance.currentStep > 1) {
            this.goTo(containerId, instance.currentStep - 1);
        }
    },

    /**
     * Mark step as completed
     * @param {string} containerId - Wizard container ID
     * @param {number} step - Step number to mark as done
     */
    markDone: function(containerId, step) {
        var instance = this.get(containerId);
        if (!instance) return;

        var stepEl = instance.container.querySelector('[data-step="' + step + '"]');
        if (stepEl) {
            stepEl.classList.add('is-done');
            
            // Update number to checkmark
            var numEl = stepEl.querySelector('.fa-wizard__number');
            if (numEl) {
                numEl.innerHTML = '<i class="fa-solid fa-check"></i>';
            }
            
            // Update divider
            var dividers = instance.container.querySelectorAll('.fa-wizard__divider');
            if (dividers[step - 1]) {
                dividers[step - 1].classList.add('is-done');
            }
        }
    },

    /**
     * Reset wizard to initial state
     * @param {string} containerId - Wizard container ID
     */
    reset: function(containerId) {
        var instance = this.get(containerId);
        if (!instance) return;

        instance.currentStep = 1;
        
        // Reset all steps
        var steps = instance.container.querySelectorAll('.fa-wizard__step');
        steps.forEach(function(el, idx) {
            el.classList.remove('is-active', 'is-done');
            var numEl = el.querySelector('.fa-wizard__number');
            if (numEl) {
                numEl.textContent = idx + 1;
            }
        });

        // Reset dividers
        var dividers = instance.container.querySelectorAll('.fa-wizard__divider');
        dividers.forEach(function(el) {
            el.classList.remove('is-done');
        });

        this._updateUI(instance);
    },

    /**
     * Get current step
     * @param {string} containerId - Wizard container ID
     * @returns {number} Current step number
     */
    getCurrentStep: function(containerId) {
        var instance = this.get(containerId);
        return instance ? instance.currentStep : 0;
    },

    /**
     * Get total number of steps
     * @param {string} containerId - Wizard container ID
     * @returns {number} Total steps
     */
    getTotalSteps: function(containerId) {
        var instance = this.get(containerId);
        if (!instance) return 0;
        return instance.container.querySelectorAll('.fa-wizard__step').length;
    },

    /**
     * Internal: Render wizard UI
     * @private
     */
    _render: function(instance) {
        // If steps are provided in options but not in DOM, render them
        if (instance.options.steps.length > 0) {
            // Clear existing content
            instance.container.innerHTML = '';
            
            instance.options.steps.forEach(function(stepLabel, idx) {
                var stepNum = idx + 1;
                var isActive = stepNum === instance.currentStep;
                var isDone = stepNum < instance.currentStep;
                
                var stepEl = document.createElement('div');
                stepEl.className = 'fa-wizard__step' + (isActive ? ' is-active' : '') + (isDone ? ' is-done' : '');
                stepEl.dataset.step = stepNum;
                stepEl.innerHTML = '<span class="fa-wizard__number">' + (isDone ? '<i class="fa-solid fa-check"></i>' : stepNum) + '</span>' +
                                   '<span class="fa-wizard__label">' + stepLabel + '</span>';
                instance.container.appendChild(stepEl);
                
                if (idx < instance.options.steps.length - 1) {
                    var divider = document.createElement('div');
                    divider.className = 'fa-wizard__divider' + (isDone ? ' is-done' : '');
                    instance.container.appendChild(divider);
                }
            });
        }
        
        this._updateUI(instance);
    },

    /**
     * Internal: Update wizard UI
     * @private
     */
    _updateUI: function(instance) {
        var steps = instance.container.querySelectorAll('.fa-wizard__step');
        
        steps.forEach(function(el, idx) {
            var stepNum = idx + 1;
            el.classList.remove('is-active');
            
            if (stepNum === instance.currentStep) {
                el.classList.add('is-active');
            }
        });

        instance.container.dataset.current = instance.currentStep;
    },

    /**
     * Internal: Attach event listeners
     * @private
     */
    _attachEvents: function(instance) {
        // Click on steps to navigate (if allowed)
        if (instance.options.allowBack) {
            instance.container.addEventListener('click', function(e) {
                var stepEl = e.target.closest('.fa-wizard__step');
                if (stepEl) {
                    var step = parseInt(stepEl.dataset.step, 10);
                    if (step < instance.currentStep) {
                        FaceAttend.UI.Wizard.goTo(instance.container.id, step);
                    }
                }
            });
        }
    }
};
