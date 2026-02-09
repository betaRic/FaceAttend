/* global $, Swal */
(function(){
  function wireSearch(inputSel, tableSel){
    $(inputSel).on("input", function(){
      const q = ($(this).val() || "").toLowerCase();
      $(tableSel).find("tbody tr").each(function(){
        const t = $(this).text().toLowerCase();
        $(this).toggle(t.indexOf(q) >= 0);
      });
    });
  }

  function wireConfirmForms(selector, title, text){
    $(document).on("submit", selector, async function(e){
      e.preventDefault();
      const form = this;

      const r = await Swal.fire({
        icon: "warning",
        title: title,
        text: text,
        showCancelButton: true,
        confirmButtonText: "Continue",
        cancelButtonText: "Cancel",
        reverseButtons: true
      });

      if (!r.isConfirmed) return;

      // normal submit after confirm
      form.submit();
    });
  }

  window.FaceAttendAdmin = {
    wireSearch,
    wireConfirmForms
  };
})();
