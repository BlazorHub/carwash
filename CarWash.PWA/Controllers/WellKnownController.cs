﻿using Microsoft.AspNetCore.Mvc;
using CarWash.ClassLibrary.Models;
using CarWash.ClassLibrary.Enums;

namespace CarWash.PWA.Controllers
{
    /// <summary>
    /// .well-known API
    /// </summary>
    [Produces("application/json")]
    [Route("api/.well-known")]
    [ApiController]
    public class WellKnownController : ControllerBase
    {
        private readonly CarWashConfiguration _configuration;

        /// <inheritdoc />
        public WellKnownController(CarWashConfiguration configuration)
        {
            _configuration = configuration;
        }

        // GET: api/.well-known/configuration
        /// <summary>
        /// Get CarWash Configuration
        /// </summary>
        /// <returns>CarWash Configuration</returns>
        /// <response code="200">OK</response>
        [HttpGet, Route("configuration")]
        public ActionResult<WellKnown> GetConfiguration()
        {
            var wellKnown = new WellKnown
            {
                Slots = _configuration.Slots,
                Companies = _configuration.Companies,
                Garages = _configuration.Garages,
                Services = ServiceTypes.Types,
                ReservationSettings = _configuration.Reservation,
            };

            return Ok(wellKnown);
        }
    }
}
