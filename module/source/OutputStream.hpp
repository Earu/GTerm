/*************************************************************************
 * MultiLibrary - danielga.bitbucket.org/multilibrary
 * A C++ library that covers multiple low level systems.
 *------------------------------------------------------------------------
 * Copyright (c) 2015, Daniel Almeida
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 * 1. Redistributions of source code must retain the above copyright
 * notice, this list of conditions and the following disclaimer.
 *
 * 2. Redistributions in binary form must reproduce the above copyright
 * notice, this list of conditions and the following disclaimer in the
 * documentation and/or other materials provided with the distribution.
 *
 * 3. Neither the name of the copyright holder nor the names of its
 * contributors may be used to endorse or promote products derived from
 * this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
 * A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
 * HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
 * LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 * DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
 * THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 *************************************************************************/

#pragma once

#include <Stream.hpp>
#include <string>

namespace MultiLibrary
{

/*!
 \brief An abstract class for objects that can act as output data streams.
 */
class OutputStream : public Stream
{
public:
	/*!
	 \brief Writes the specified amount of bytes from the provided buffer.

	 \param data Data to write.
	 \param size Size of the data.

	 \return Amount of written bytes.
	 */
	virtual size_t Write( const void *data, size_t size ) = 0;

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const bool &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const int8_t &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const uint8_t &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const int16_t &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const uint16_t &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const int32_t &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const uint32_t &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const int64_t &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const uint64_t &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const float &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const double &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const char &data );

	/*!
	 \brief Write data into the buffer from an array.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const char *data );

	/*!
	 \brief Write data into the buffer from an object.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const std::string &data );

	/*!
	 \brief Write data into the buffer from a variable.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const wchar_t &data );

	/*!
	 \brief Write data into the buffer from an array.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const wchar_t *data );

	/*!
	 \brief Write data into the buffer from an object.

	 \param data Data to write.

	 \return This object.

	 \overload
	 */
	virtual OutputStream &operator<<( const std::wstring &data );
};

} // namespace MultiLibrary