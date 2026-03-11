using System;

namespace FaceAttend.Services
{
    /// <summary>
    /// SAGUPA: Utility class para sa geographic calculations (GPS distance).
    /// 
    /// PAGLALARAWAN (Description):
    ///   Nagbibigay ng mga helper methods para sa:
    ///   - Distance calculation sa pagitan ng dalawang GPS coordinates
    ///   - Office radius validation (check kung nasa loob ng allowed area)
    /// 
    /// GINAGAMIT SA:
    ///   - KioskController.ResolveOffice() - para malaman kung nasa office ba
    ///   - PickOffice() - para hanapin ang pinakamalapit na office
    /// 
    /// FORMULA:
    ///   Haversine formula - pinakatumpak para sa short distances (< 1km)
    ///   Earth radius: 6,371 km (average)
    /// 
    /// EXAMPLE USAGE:
    ///   double distance = GeoUtil.DistanceMeters(
    ///       6.125, 125.175,  // Employee GPS
    ///       6.130, 125.180   // Office GPS
    ///   );
    ///   // Result: ~780 meters
    /// 
    /// ILOKANO: "Ti Haversine formula ket usaren na a pakabailan ti agpang 
    ///           iti nagbaetan ti dua a punto iti globo"
    /// </summary>
    public static class GeoUtil
    {
        /// <summary>
        /// Kinakalkula ang distance sa pagitan ng dalawang GPS coordinates
        /// gamit ang Haversine formula. Resulta ay sa meters.
        /// 
        /// TAGALOG: Kinukuha ang layo sa pagitan ng dalawang punto sa globo.
        ///         Ginagamit para malaman kung malapit ba ang empleyado sa office.
        /// </summary>
        /// <param name="lat1">Latitude ng unang punto (empleyado)</param>
        /// <param name="lon1">Longitude ng unang punto (empleyado)</param>
        /// <param name="lat2">Latitude ng ikalawang punto (office)</param>
        /// <param name="lon2">Longitude ng ikalawang punto (office)</param>
        /// <returns>Distance sa meters</returns>
        public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0; // Earth radius meters
            double dLat = ToRad(lat2 - lat1);
            double dLon = ToRad(lon2 - lon1);

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private static double ToRad(double deg) { return deg * (Math.PI / 180.0); }
    }
}
