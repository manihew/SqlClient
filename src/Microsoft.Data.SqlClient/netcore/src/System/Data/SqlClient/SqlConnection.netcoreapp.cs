﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Data.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;

namespace Microsoft.Data.SqlClient
{
    public sealed partial class SqlConnection : DbConnection, ICloneable
    {
        static SqlConnection()
        {
            SqlColumnEncryptionEnclaveProviderConfigurationSection sqlColumnEncryptionEnclaveProviderConfigurationSection = null;
            try
            {
                sqlColumnEncryptionEnclaveProviderConfigurationSection = (SqlColumnEncryptionEnclaveProviderConfigurationSection)ConfigurationManager.GetSection("SqlColumnEncryptionEnclaveProviders");
            }
            catch (ConfigurationErrorsException e)
            {
                throw SQL.CannotGetSqlColumnEncryptionEnclaveProviderConfig(e);
            }

            sqlColumnEncryptionEnclaveProviderConfigurationManager = new SqlColumnEncryptionEnclaveProviderConfigurationManager(sqlColumnEncryptionEnclaveProviderConfigurationSection);
        }

        static internal readonly SqlColumnEncryptionEnclaveProviderConfigurationManager sqlColumnEncryptionEnclaveProviderConfigurationManager;

        // System column encryption key store providers are added by default
        static private readonly Dictionary<string, SqlColumnEncryptionKeyStoreProvider> _SystemColumnEncryptionKeyStoreProviders
            = new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>(capacity: 1, comparer: StringComparer.OrdinalIgnoreCase)
            {
                {SqlColumnEncryptionCertificateStoreProvider.ProviderName, new SqlColumnEncryptionCertificateStoreProvider()},
                {SqlColumnEncryptionCngProvider.ProviderName, new SqlColumnEncryptionCngProvider()},
                {SqlColumnEncryptionCspProvider.ProviderName, new SqlColumnEncryptionCspProvider()}
            };


        static public TimeSpan ColumnEncryptionKeyCacheTtl { get; set; } = TimeSpan.FromHours(2);

        /// <summary>
        /// Custom provider list should be provided by the user. We shallow copy the user supplied dictionary into a ReadOnlyDictionary.
        /// Custom provider list can only supplied once per application.
        /// </summary>
        static private ReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider> _CustomColumnEncryptionKeyStoreProviders;

        // Lock to control setting of _CustomColumnEncryptionKeyStoreProviders
        static private readonly Object _CustomColumnEncryptionKeyProvidersLock = new Object();

        /// <summary>
        /// Dictionary object holding trusted key paths for various SQL Servers.
        /// Key to the dictionary is a SQL Server Name
        /// IList contains a list of trusted key paths.
        /// </summary>
        static private readonly ConcurrentDictionary<string, IList<string>> _ColumnEncryptionTrustedMasterKeyPaths
            = new ConcurrentDictionary<string, IList<string>>(concurrencyLevel: 4 * Environment.ProcessorCount /* default value in ConcurrentDictionary*/,
                capacity: 1,
                comparer: StringComparer.OrdinalIgnoreCase);

        static public IDictionary<string, IList<string>> ColumnEncryptionTrustedMasterKeyPaths
        {
            get
            {
                return _ColumnEncryptionTrustedMasterKeyPaths;
            }
        }

        /// <summary>
        /// This function should only be called once in an app. This does shallow copying of the dictionary so that 
        /// the app cannot alter the custom provider list once it has been set.
        /// 
        /// Example:
        /// 
        /// Dictionary<string, SqlColumnEncryptionKeyStoreProvider> customKeyStoreProviders = new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>();
        /// MySqlClientHSMProvider myProvider = new MySqlClientHSMProvider();
        /// customKeyStoreProviders.Add(@"HSM Provider", myProvider);
        /// SqlConnection.RegisterColumnEncryptionKeyStoreProviders(customKeyStoreProviders);
        /// </summary>
        /// <param name="customProviders">Custom column encryption key provider dictionary</param>
        static public void RegisterColumnEncryptionKeyStoreProviders(IDictionary<string, SqlColumnEncryptionKeyStoreProvider> customProviders)
        {

            // Return when the provided dictionary is null.
            if (customProviders == null)
            {
                throw SQL.NullCustomKeyStoreProviderDictionary();
            }

            // Validate that custom provider list doesn't contain any of system provider list
            foreach (string key in customProviders.Keys)
            {
                // Validate the provider name
                //
                // Check for null or empty
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw SQL.EmptyProviderName();
                }

                // Check if the name starts with MSSQL_, since this is reserved namespace for system providers.
                if (key.StartsWith(ADP.ColumnEncryptionSystemProviderNamePrefix, StringComparison.InvariantCultureIgnoreCase))
                {
                    throw SQL.InvalidCustomKeyStoreProviderName(key, ADP.ColumnEncryptionSystemProviderNamePrefix);
                }

                // Validate the provider value
                if (customProviders[key] == null)
                {
                    throw SQL.NullProviderValue(key);
                }
            }

            lock (_CustomColumnEncryptionKeyProvidersLock)
            {
                // Provider list can only be set once
                if (_CustomColumnEncryptionKeyStoreProviders != null)
                {
                    throw SQL.CanOnlyCallOnce();
                }

                // Create a temporary dictionary and then add items from the provided dictionary.
                // Dictionary constructor does shallow copying by simply copying the provider name and provider reference pairs
                // in the provided customerProviders dictionary.
                Dictionary<string, SqlColumnEncryptionKeyStoreProvider> customColumnEncryptionKeyStoreProviders =
                    new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>(customProviders, StringComparer.OrdinalIgnoreCase);

                // Set the dictionary to the ReadOnly dictionary.
                _CustomColumnEncryptionKeyStoreProviders = new ReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider>(customColumnEncryptionKeyStoreProviders);
            }
        }

        /// <summary>
        /// This function walks through both system and custom column encryption key store providers and returns an object if found.
        /// </summary>
        /// <param name="providerName">Provider Name to be searched in System Provider diction and Custom provider dictionary.</param>
        /// <param name="columnKeyStoreProvider">If the provider is found, returns the corresponding SqlColumnEncryptionKeyStoreProvider instance.</param>
        /// <returns>true if the provider is found, else returns false</returns>
        static internal bool TryGetColumnEncryptionKeyStoreProvider(string providerName, out SqlColumnEncryptionKeyStoreProvider columnKeyStoreProvider)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(providerName), "Provider name is invalid");

            // Initialize the out parameter
            columnKeyStoreProvider = null;

            // Search in the sytem provider list.
            if (_SystemColumnEncryptionKeyStoreProviders.TryGetValue(providerName, out columnKeyStoreProvider))
            {
                return true;
            }

            lock (_CustomColumnEncryptionKeyProvidersLock)
            {
                // If custom provider is not set, then return false
                if (_CustomColumnEncryptionKeyStoreProviders == null)
                {
                    return false;
                }

                // Search in the custom provider list
                return _CustomColumnEncryptionKeyStoreProviders.TryGetValue(providerName, out columnKeyStoreProvider);
            }
        }

        /// <summary>
        /// This function returns a list of system provider dictionary currently supported by this driver.
        /// </summary>
        /// <returns>Combined list of provider names</returns>
        static internal List<string> GetColumnEncryptionSystemKeyStoreProviders()
        {
            HashSet<string> providerNames = new HashSet<string>(_SystemColumnEncryptionKeyStoreProviders.Keys);
            return providerNames.ToList();
        }

        /// <summary>
        /// This function returns a list of custom provider dictionary currently registered.
        /// </summary>
        /// <returns>Combined list of provider names</returns>
        static internal List<string> GetColumnEncryptionCustomKeyStoreProviders()
        {
            if (_CustomColumnEncryptionKeyStoreProviders != null)
            {
                HashSet<string> providerNames = new HashSet<string>(_CustomColumnEncryptionKeyStoreProviders.Keys);
                return providerNames.ToList();
            }

            return new List<string>();
        }

    }
}