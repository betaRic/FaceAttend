# FaceAttend Bundle Migration Summary

**Date:** 2025-01-15  
**Status:** ✅ **COMPLETE**

---

## Simplified File Structure

### CSS (Single File)
```
Content/
└── facescan-v2.css          ← Unified design system (all components in one file)
```

### JavaScript (Single File)
```
Scripts/
└── facescan-ui.js           ← UI components (Wizard, Uploader, Modal in one file)
```

---

## New Bundle Names

| Bundle Type | Old Bundle | New Bundle | Purpose |
|-------------|------------|------------|---------|
| **CSS** | `~/Content/facescan` | `~/Content/facescan-v2` | Unified design system |
| **JS** | `~/bundles/unified-ui` | `~/bundles/facescan-ui` | UI components |
| **JS** | (multiple) | `~/bundles/enrollment` | Full enrollment bundle |

---

## Updated Views

### Mobile Views (Views/MobileRegistration/)
| View | CSS Bundle | JS Bundle |
|------|------------|-----------|
| `Enroll.cshtml` | `~/Content/facescan-v2` | `~/bundles/enrollment` |
| `Identify.cshtml` | `~/Content/facescan-v2` | `~/bundles/facescan-core` |
| `Index.cshtml` | `~/Content/facescan-v2` | (layout) |
| `Device.cshtml` | `~/Content/facescan-v2` | (layout) |
| `Success.cshtml` | `~/Content/facescan-v2` | (layout) |

### Admin Views (Areas/Admin/Views/)
| View | CSS Bundle | JS Bundle |
|------|------------|-----------|
| `Employees/Enroll.cshtml` | `~/Content/facescan-v2` | `~/bundles/facescan-core` + `~/bundles/facescan-ui` |
| `Visitors/Enroll.cshtml` | `~/Content/facescan-v2` | `~/bundles/facescan-core` + `~/bundles/facescan-ui` |

### Shared Layouts (Views/Shared/)
| Layout | CSS Bundle |
|--------|------------|
| `_MobileLayout.cshtml` | `~/Content/facescan-v2` |
| `_EnrollmentLayout.cshtml` | `~/Content/facescan-v2` |

### Kiosk Views
| View | CSS Bundle |
|------|------------|
| `Kiosk/Index.cshtml` | `~/Content/facescan-v2` + `~/Content/kiosk` |

---

## Usage Guide

### For Enrollment Pages:
```csharp
@section styles {
    @Styles.Render("~/Content/facescan-v2")
}

@section scripts {
    @Scripts.Render("~/bundles/facescan-core")
    @Scripts.Render("~/bundles/facescan-ui")
}
```

### For Full Enrollment (with enrollment logic):
```csharp
@section styles {
    @Styles.Render("~/Content/facescan-v2")
}

@section scripts {
    @Scripts.Render("~/bundles/enrollment")  // Includes core + UI + enrollment logic
}
```

### For Mobile Pages:
```csharp
@section styles {
    @Styles.Render("~/Content/facescan-v2")
}
```
(Scripts are included in `_MobileLayout.cshtml`)

---

## Available JavaScript APIs

After including `~/bundles/facescan-ui`:

```javascript
// Wizard
FaceAttend.UI.Wizard.init('wizardId', { steps: ['Step1', 'Step2'] });
FaceAttend.UI.Wizard.goTo('wizardId', 2);
FaceAttend.UI.Wizard.next('wizardId');
FaceAttend.UI.Wizard.prev('wizardId');

// Uploader
FaceAttend.UI.Uploader.init('uploaderId', { maxFiles: 5 });
FaceAttend.UI.Uploader.getFiles('uploaderId');
FaceAttend.UI.Uploader.clear('uploaderId');

// Modal
FaceAttend.UI.Modal.show('modalId');
FaceAttend.UI.Modal.hide('modalId');
FaceAttend.UI.Modal.showProcessing('modalId', { title: 'Processing...' });
FaceAttend.UI.Modal.alert('Title', 'Message', 'success');
FaceAttend.UI.Modal.confirm({ title: 'Confirm?', message: 'Are you sure?' });
```

---

## CSS Classes Available

After including `~/Content/facescan-v2`:

### Wizard
- `.fa-wizard` - Container
- `.fa-wizard__step` - Step item
- `.fa-wizard__step.is-active` - Active step
- `.fa-wizard__step.is-done` - Completed step
- `.fa-wizard__divider` - Divider between steps

### File Uploader
- `.fa-uploader` - Container
- `.fa-uploader__dropzone` - Drop zone
- `.fa-uploader__dropzone.is-dragover` - Drag over state
- `.fa-uploader__files` - File list container
- `.fa-uploader__chip` - File chip
- `.fa-uploader__error` - Error message

### Modal
- `.fa-modal` - Modal container
- `.fa-modal.is-open` - Open state
- `.fa-modal__backdrop` - Backdrop
- `.fa-modal__content` - Content container
- `.fa-modal--processing` - Processing variant
- `.fa-modal--success` - Success variant
- `.fa-modal__progress` - Progress bar container
- `.fa-modal__progress-bar` - Progress bar

### Method Selector
- `.fa-method-grid` - Grid container
- `.fa-method-card` - Method card
- `.fa-method__icon` - Icon container
- `.fa-method__title` - Title
- `.fa-method__desc` - Description
- `.fa-method__badge` - Badge (e.g., "Recommended")

### Camera
- `.fa-camera` - Camera container
- `.fa-camera__video` - Video element
- `.fa-camera__guide` - Face guide overlay
- `.fa-camera__guide.is-detected` - Face detected state
- `.fa-camera__status` - Status badge

---

## Verification Checklist

- [x] All mobile views use `~/Content/facescan-v2`
- [x] All admin enrollment views use `~/Content/facescan-v2`
- [x] All layouts use `~/Content/facescan-v2`
- [x] JavaScript bundles use simplified names
- [x] BundleConfig.cs updated with new bundles
- [x] Legacy bundles preserved for backward compatibility

---

## Migration Complete! ✅

All views now use the simplified unified bundles:
- **Single CSS file:** `facescan-v2.css`
- **Single JS file:** `facescan-ui.js`
- **Consistent naming:** `~/Content/facescan-v2` and `~/bundles/facescan-ui`
