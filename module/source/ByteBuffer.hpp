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

#include <IOStream.hpp>
#include <string>
#include <vector>
#include <set>

namespace MultiLibrary
{
/*!
 \brief A class that represents a buffer composed by bytes.

 It provides useful functions to read and write to it. Works very much
 like a file.
 */
class ByteBuffer : public IOStream
{
public:
	/*!
	 \brief Default constructor.
	 */
	ByteBuffer( );

	/*!
	 \brief Create a buffer with the specified size.

	 \param size Initial size of the buffer

	 \overload
	 */
	ByteBuffer( size_t size );

	/*!
	 \brief Create a buffer from the provided data.

	 \param copy_buffer Buffer to copy the data from.
	 \param size Size of the buffer provided

	 \sa Assign

	 \overload
	 */
	ByteBuffer( const uint8_t *copy_buffer, size_t size );

	/*!
	 \brief Destructor.

	 Subscribers are alerted to this object destruction, if any.
	 */
	~ByteBuffer( );

	/*!
	 \brief Tell if the buffer is valid.

	 Currently just checks if we reached the end of the buffer.

	 \return If we haven't reached the end of the buffer, true, otherwise false.

	 \sa EndOfFile
	 */
	bool IsValid( ) const;

	/*!
	 \brief Tell if the object is valid.

	 Currently just returns the value of IsValid.

	 \return A boolean type relative to IsValid.

	 \sa IsValid
	 */
	explicit operator bool( ) const;

	/*!
	 \brief Tell if the object is not valid.

	 Currently just returns the reverse of IsValid.

	 \return Validness of this object.

	 \sa IsValid
	 */
	bool operator!( ) const;

	/*!
	 \brief Return the current position on the buffer.

	 \return Current position of read/write operations on the buffer.
	 */
	int64_t Tell( ) const;

	/*!
	 \brief Return the size of the buffer.

	 This value is totally independent of the capacity and both mean two
	 different things.

	 \return Size of the buffer.

	 \sa Capacity
	 */
	int64_t Size( ) const;

	/*!
	 \brief Return the capacity of this object.

	 This value is totally independent of the size and both mean two
	 different things.

	 \return Capacity of this object.

	 \sa Size
	 */
	size_t Capacity( ) const;

	/*!
	 \brief Set the current position of read/write operations.

	 Currently this operation is always successful.

	 \param position Position to set the pointer to.
	 \param mode (Optional) Type of seeking pretended.

	 \return Success of this operation.
	 */
	bool Seek( int64_t position, SeekMode mode = SEEKMODE_SET );

	/*!
	 \brief Tell if the end of file was reached.

	 In this case, end of file means we reached the end of the buffer.

	 \return End of buffer reached.
	 */
	bool EndOfFile( ) const;

	/*!
	 \brief Return pointer to internal buffer.

	 \return Pointer to internal buffer.
	 */
	uint8_t *GetBuffer( );

	/*!
	 \brief Return const pointer to internal buffer.

	 \return Pointer to internal buffer.

	 \overload
	 */
	const uint8_t *GetBuffer( ) const;

	/*!
	 \brief Reset the buffer.

	 All data is wiped and all flags reset.
	 */
	void Clear( );

	/*!
	 \brief Increase the capacity of the internal buffer.

	 It isn't possible to decrease the capacity.

	 \param capacity New capacity of the internal buffer.
	 */
	void Reserve( size_t capacity );

	/*!
	 \brief Resize the internal buffer.

	 \param size New size of the internal buffer.
	 */
	void Resize( size_t size );

	/*!
	 \brief Shrinks the internal buffer capacity to its size.
	 */
	void ShrinkToFit( );

	/*!
	 \brief Assign data to the internal buffer.

	 Copies the data to the internal buffer. Current size is also changed
	 to the size of the data provided.

	 \param copy_buffer Buffer to copy the data from.
	 \param size Size of the buffer provided.
	 */
	void Assign( const uint8_t *copy_buffer, size_t size );

	/*!
	 \brief Read data from the buffer.

	 \param value Pointer to the buffer to write to.
	 \param size Amount to read.

	 \return Size in bytes of the read data.
	 */
	size_t Read( void *value, size_t size );

	/*!
	 \brief Write data to the buffer.

	 \param value Pointer to the data to write.
	 \param size Size of the provided data.

	 \return Size in bytes of the written data.
	 */
	size_t Write( const void *value, size_t size );

private:
	bool end_of_file;
	std::vector<uint8_t> buffer_internal;
	size_t buffer_offset;
};

} // namespace MultiLibrary