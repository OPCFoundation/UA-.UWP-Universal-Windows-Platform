/* Copyright (c) 1996-2020 The OPC Foundation. All rights reserved.
   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation members in good-standing
     - GPL V2: everybody else
   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/
   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2
   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

namespace Opc.Ua
{
    /// <summary>
    /// Supports implementation for custom certificate store type.
    /// </summary>
    public interface ICertificateStoreType
    {
        /// <summary>
        /// Determines if the store path is supported by the store type.
        /// </summary>
        /// <param name="storePath">The store path to examine.</param>
        /// <returns><see langword="true"/> if the store type supports the given store path, otherwise <see langword="false"/>.</returns>
        bool SupportsStorePath(string storePath);

        /// <summary>
        /// Creates a new certificate store.
        /// </summary>
        /// <returns>A reference to the new certificate store object</returns>
        ICertificateStore CreateStore();
    }
}
